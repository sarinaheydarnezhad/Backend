global using Program = Cent.TestHost.CentTestEntry;
using Xunit;

// The existing factory temporarily sets process-wide environment variables during host startup.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
