using IAM.Modules.Catalog.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Catalog.Endpoints;

/// <summary>
/// Composition entry point for the Catalog module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// Registering the module makes the host require <c>ConnectionStrings:catalog</c> (ADR-0017).
/// </summary>
public static class CatalogModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/catalog";

    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCatalogInfrastructure();
        services.AddExceptionHandler<CatalogExceptionHandler>();
        services.AddSingleton<FluentValidation.IValidator<RegisterApplicationBody>, RegisterApplicationBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<RenameApplicationBody>, RenameApplicationBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<UpdateSettingsDraftBody>, UpdateSettingsDraftBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<NewSettingsDraftBody>, NewSettingsDraftBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<CatalogReasonBody>, CatalogReasonBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<ApproveSettingsBody>, ApproveSettingsBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<ProposeRoleTypeBody>, ProposeRoleTypeBodyValidator>();
        return services;
    }

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // DL-066: the application catalog, with activation and owner or role changes decided by a second person.
        ApplicationCatalogEndpoints.Map(endpoints);
        return endpoints;
    }
}
