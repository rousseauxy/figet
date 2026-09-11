using FiGet.Web;

var builder = WebApplication.CreateBuilder(args);
FiGetApp.ConfigureServices(builder);
var app = await FiGetApp.BuildAsync(builder);
await app.RunAsync();

/// <summary>Entry point, public so test hosts can reference the assembly.</summary>
public partial class Program;
