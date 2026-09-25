using Xunit;

// These tests are not unit tests wearing a suite's clothes. Every class here stands up a real
// BanterServer on a real socket with a real SQLite file, connects real clients to it, and then
// measures something against the wall clock - nineteen of the twenty files in this project call
// Task.Delay. xUnit's default is to run classes in parallel up to the processor count, which on
// CI's two cores means two of these running at once, each expecting the thread pool to answer
// promptly while the other is doing the same.
//
// That is what has been failing. Three tests here have been "fixed" one at a time for racing the
// machine they run on, and the fourth (KeepAliveTests) went red on a run where the whole project
// took 33 seconds of a 100-second suite - so the contention was not idle. Serialising costs a
// little wall clock and buys back a class of failure that looks like a product bug and is not.
//
// This is deliberately scoped to this project. Banter.App.Tests is 615 mostly-pure tests and wants
// the parallelism; the solution-wide half of the same problem is the -m:1 in CI, which stops the
// test PROJECTS fighting each other but says nothing about what happens inside one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
