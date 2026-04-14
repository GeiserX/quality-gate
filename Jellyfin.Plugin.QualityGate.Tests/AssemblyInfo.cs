using Xunit;

// Plugin.Instance is a static singleton shared by all tests.
// Disable parallelization to prevent test classes from stomping on each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
