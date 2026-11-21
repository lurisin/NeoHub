// Several tests use timing, process-wide static state (for example the trouble refresh floor) or
// session lifecycles, so the test classes run one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
