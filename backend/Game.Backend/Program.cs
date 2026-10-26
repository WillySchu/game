using RaidZero.Backend;

var app = BackendApp.Build(args);
await BackendApp.InitializeAsync(app);
app.Run();
