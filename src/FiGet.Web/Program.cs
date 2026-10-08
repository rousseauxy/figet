using FiGet.Web;
using FiGet.Web.Security;

// First, before anything can create a file: two clusters share this volume under different user ids and the same
// group, so a file the group cannot write is a file the other cluster can never replace or delete.
ProcessUmask.AllowTheGroup();

var builder = WebApplication.CreateBuilder(args);
FiGetApp.ConfigureServices(builder);
var app = await FiGetApp.BuildAsync(builder);
await app.RunAsync();

/// <summary>Entry point, public so test hosts can reference the assembly.</summary>
public partial class Program;
