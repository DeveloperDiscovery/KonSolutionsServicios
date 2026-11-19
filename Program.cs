using KonSolutionsServicios;
using KonSolutions.XmlRecibidos;

// Servicio de Windows generico «KonSolutionsServicios»: aloja tareas programadas (ITareaProgramada). Tambien corre como consola (dotnet run / .exe) para probar.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "KonSolutionsServicios");
builder.Configuration.AddEnvironmentVariables();
// todo lo que anota el servicio, tambien en un archivo por dia (carpeta «logs» al lado del .exe o Servicio:CarpetaLogs)
var carpetaLogs = builder.Configuration["Servicio:CarpetaLogs"];
builder.Logging.AddProvider(new FileLoggerProvider(string.IsNullOrWhiteSpace(carpetaLogs) ? Path.Combine(AppContext.BaseDirectory, "logs") : carpetaLogs.Trim(), builder.Configuration.GetValue("Logging:LogLevel:Default", LogLevel.Information)));

// --- Comun a todos los servicios ---
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ErrorLogService>();
builder.Services.AddScoped<IPathFilesRepository, PathFilesRepository>();

// --- Servicio: XmlRecibidos (Servicios/XmlRecibidos) ---
builder.Services.Configure<XmlRecibidosOpciones>(builder.Configuration.GetSection("XmlRecibidos"));
builder.Services.AddSingleton<GraphTokenCache>();
builder.Services.AddHttpClient<IGraphCorreo, GraphCorreo>();
builder.Services.Configure<SunatOpciones>(builder.Configuration.GetSection("Sunat"));
builder.Services.AddHttpClient<ISunatValidador, SunatValidador>();
builder.Services.AddScoped<IXmlRecibidosRepository, XmlRecibidosRepository>();
builder.Services.AddScoped<IXmlRecibidosService, XmlRecibidosService>();
builder.Services.AddSingleton<XmlRecibidosEstado>();
builder.Services.AddSingleton<ITareaProgramada, XmlRecibidosTarea>();

// --- Servicio futuro: crea Servicios/<Nombre>/ con su tarea y sus clases, y registralo aqui ---
// builder.Services.AddSingleton<ITareaProgramada, MiNuevaTarea>();

builder.Services.AddHostedService<TareasWorker>();

await builder.Build().RunAsync();
