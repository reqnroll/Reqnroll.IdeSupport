using Xunit;

// ConcurrencyProbeTests (Performance/ConcurrencyProbeTests.cs) measures real wall-clock latency
// under a deliberately induced concurrent load. xUnit's default cross-class parallelization means
// that measurement previously shared the CPU with the other ~830 tests in this assembly, which
// contaminates it with unrelated contention noise having nothing to do with the LSP dispatch
// pipeline it's meant to observe -- confirmed by a CI failure where the measured ratio swung from
// a consistent ~15x-20x in isolation to as low as ~1.3x under full-assembly parallel load in one
// run and back up to ~40x+ in another. Disabling parallelization for the whole assembly costs
// real wall-clock time (~2x locally) but is the only reliable way to keep that one benchmark's
// numbers meaningful without moving it into its own test project.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
