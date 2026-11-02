using Lims.Host;

if (LimsCli.IsCommand(args)) return await LimsCli.RunAsync(args, Console.In, Console.Out, Console.Error);

var app = LimsApp.Build(args);
await LimsApp.InitializeAsync(app);
await app.RunAsync();
return 0;

public partial class Program;
