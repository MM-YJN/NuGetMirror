namespace NuGetMirror.E2E.Tests;

/// <summary>
/// Groups all end-to-end test classes into a single xUnit collection that shares
/// one <see cref="KestrelMirrorFixture"/> instance. The classes therefore run
/// sequentially against a single mirror, which also avoids registering the global
/// <c>TestcontainersSettings.ExposeHostPortsAsync</c> host-port forwarder more than
/// once (a second registration for a different port fails).
/// </summary>
[CollectionDefinition("E2E")]
public sealed class E2ECollection : ICollectionFixture<KestrelMirrorFixture>;
