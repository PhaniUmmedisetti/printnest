using Xunit;

namespace PrintNest.IntegrationTests.Infrastructure;

[CollectionDefinition("Integration", DisableParallelization = true)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationContainerFixture>
{
}
