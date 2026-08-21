// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Runtime.CompilerServices;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemEnumeratorCancellation = System.Runtime.CompilerServices.EnumeratorCancellationAttribute;
using SystemTask = System.Threading.Tasks.Task;

namespace Microsoft.Coyote.Rewriting.Types.Threading.Tasks
{
    /// <summary>
    /// Stores the state required to asynchronously enumerate a collection of tasks in the
    /// order that they complete during systematic testing.
    /// </summary>
    /// <remarks>
    /// The uncontrolled <see cref="SystemTask.WhenEach(SystemTask[])"/> methods signal the
    /// enumeration from an uncontrolled thread pool thread, which the runtime is unable to
    /// observe, so awaiting the enumeration can result in a false deadlock. The enumeration
    /// is instead performed using controlled operations that pause until the next task
    /// completes, which preserves the completion order of the enumerated tasks.
    /// </remarks>
    internal sealed class WhenEachState
    {
        /// <summary>
        /// Responsible for controlling the enumeration of the tasks.
        /// </summary>
        private readonly CoyoteRuntime Runtime;

        /// <summary>
        /// Synchronizes access to the enumerated tasks.
        /// </summary>
        private readonly object SyncObject;

        /// <summary>
        /// The tasks that have not completed yet, in the order that they were specified.
        /// </summary>
        private readonly List<SystemTask> Pending;

        /// <summary>
        /// The tasks that have completed, but have not been yielded yet, in completion order.
        /// </summary>
        private readonly Queue<SystemTask> Completed;

        /// <summary>
        /// Value 0 if this state has never been enumerated, else 1.
        /// </summary>
        private int Enumerated;

        /// <summary>
        /// True if all tasks have been yielded, else false.
        /// </summary>
        private bool IsEnumerationCompleted
        {
            get
            {
                lock (this.SyncObject)
                {
                    return this.Pending.Count is 0 && this.Completed.Count is 0;
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WhenEachState"/> class.
        /// </summary>
        private WhenEachState(CoyoteRuntime runtime)
        {
            this.Runtime = runtime;
            this.SyncObject = new object();
            this.Pending = new List<SystemTask>();
            this.Completed = new Queue<SystemTask>();
            this.Enumerated = 0;
        }

        /// <summary>
        /// Creates the state for enumerating the specified tasks, or null if there are no tasks.
        /// </summary>
        internal static WhenEachState Create<TTask>(CoyoteRuntime runtime, ReadOnlySpan<TTask> tasks)
            where TTask : SystemTask
        {
            WhenEachState state = null;
            if (tasks.Length != 0)
            {
                state = new WhenEachState(runtime);
                foreach (TTask task in tasks)
                {
                    if (task is null)
                    {
                        throw new ArgumentException("The tasks argument included a null value.", nameof(tasks));
                    }

                    state.Pending.Add(task);
                }
            }

            return state;
        }

        /// <summary>
        /// Creates the state for enumerating the specified tasks, or null if there are no tasks.
        /// </summary>
        internal static WhenEachState Create<TTask>(CoyoteRuntime runtime, IEnumerable<TTask> tasks)
            where TTask : SystemTask
        {
            ArgumentNullException.ThrowIfNull(tasks);

            WhenEachState state = null;
            foreach (TTask task in tasks)
            {
                if (task is null)
                {
                    throw new ArgumentException("The tasks argument included a null value.", nameof(tasks));
                }

                state ??= new WhenEachState(runtime);
                state.Pending.Add(task);
            }

            return state;
        }

        /// <summary>
        /// Asynchronously enumerates the tasks of the specified state as they complete.
        /// </summary>
        internal static async IAsyncEnumerable<TTask> Iterate<TTask>(WhenEachState state,
            [SystemEnumeratorCancellation] SystemCancellationToken cancellationToken = default)
            where TTask : SystemTask
        {
            // No matter how many times the enumerable is enumerated, each task is yielded only once,
            // which is the same behavior as the uncontrolled 'Task.WhenEach' methods.
            if (state?.TryStartEnumeration() is not true)
            {
                yield break;
            }

            while (true)
            {
                if (state.TryDequeueCompletedTask(out SystemTask next))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return (TTask)next;
                    continue;
                }

                if (state.IsEnumerationCompleted)
                {
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Pause the current operation until the next task completes, or until cancellation is
                // requested, so that the runtime remains in control of the asynchronous enumeration.
                await AsyncConditionAwaiterStateMachine.RunAsync(state.Runtime,
                    () => state.HasCompletedTask() || cancellationToken.IsCancellationRequested,
                    debugMsg: "any of the enumerated tasks to complete");
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Returns true if this state has not been enumerated before, else false.
        /// </summary>
        private bool TryStartEnumeration() => Interlocked.Exchange(ref this.Enumerated, 1) is 0;

        /// <summary>
        /// Tries to dequeue the next task that completed, but has not been yielded yet.
        /// </summary>
        private bool TryDequeueCompletedTask(out SystemTask task)
        {
            lock (this.SyncObject)
            {
                this.CheckCompletedTasks();
                if (this.Completed.Count > 0)
                {
                    task = this.Completed.Dequeue();
                    return true;
                }
            }

            task = null;
            return false;
        }

        /// <summary>
        /// Returns true if there is at least one task that completed, but has not been yielded yet.
        /// </summary>
        /// <remarks>
        /// The runtime invokes this each time that it checks if the paused enumeration can resume,
        /// which captures the tasks in the order that they complete.
        /// </remarks>
        private bool HasCompletedTask()
        {
            lock (this.SyncObject)
            {
                this.CheckCompletedTasks();
                return this.Completed.Count > 0;
            }
        }

        /// <summary>
        /// Moves any tasks that completed since the previous check to the completed tasks.
        /// </summary>
        private void CheckCompletedTasks()
        {
            for (int idx = 0; idx < this.Pending.Count; idx++)
            {
                SystemTask task = this.Pending[idx];
                if (task.IsCompleted)
                {
                    this.Completed.Enqueue(task);
                    this.Pending.RemoveAt(idx);
                    idx--;
                }
            }
        }
    }
}
#endif
