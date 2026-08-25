// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    public class UnsupportedSynchronizationRewritingTests : BaseRewritingTest
    {
        public UnsupportedSynchronizationRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestBarrierIsReportedAsUncontrolledSynchronization()
        {
            this.TestWithError(() =>
            {
                using var barrier = new Barrier(1);
                barrier.SignalAndWait();
            },
            errorChecker: (e) =>
            {
                Assert.StartsWith(
                    $"Invoking '{typeof(Barrier).FullName}..ctor' is not intercepted",
                    e);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestCountdownEventIsReportedAsUncontrolledSynchronization()
        {
            this.TestWithError(() =>
            {
                using var countdown = new CountdownEvent(1);
                countdown.Signal();
                countdown.Wait();
            },
            errorChecker: (e) =>
            {
                Assert.StartsWith(
                    $"Invoking '{typeof(CountdownEvent).FullName}..ctor' is not intercepted",
                    e);
            });
        }
    }
}
