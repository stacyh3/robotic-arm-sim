var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.RoboticArmServer>("robotic-arm-server")
	.WithHttpEndpoint(port: 3000)
	.WithEndpoint("http", endpoint => endpoint.IsProxied = false);

builder.Build().Run();
