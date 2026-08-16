using LocalCam.Server;

await using var server = await LocalCamServerHost.StartAsync(args);
await server.WaitForShutdownAsync();
