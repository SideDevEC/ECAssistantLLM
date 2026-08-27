using Xunit;

namespace ECAssistant.LLM.Tests;

/// <summary>
/// Shared collection so all integration tests use the single
/// <see cref="Fixtures.TestServerFixture"/> instance (one server, one port).
/// </summary>
[CollectionDefinition("Server")]
public class ServerCollection : ICollectionFixture<Fixtures.TestServerFixture>
{
      // This class is never instantiated; it only serves to name the collection.
}
