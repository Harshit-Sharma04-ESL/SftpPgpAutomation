
using Microsoft.Extensions.Configuration;
using SftpPgpAutomation;
using SftpPgpAutomation.Services;
using SftpPgpAutomation.Settings;

// Build configuration
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

// Initialize services
var configService = new ConfigurationService(config);
var excelStagingService = new ExcelStagingService(configService);
var fileProcessingService = new FileProcessingService(configService, excelStagingService);
var loggingService = new LoggingService(config);
var mailService = new MailService(config, configService);
var nuanceDownloadService = new NuanceDownloadService();
var coordinator = new ExecutionCoordinator(configService, fileProcessingService, loggingService, mailService, nuanceDownloadService);

// Execute application
Environment.ExitCode = await coordinator.ExecuteAsync();
