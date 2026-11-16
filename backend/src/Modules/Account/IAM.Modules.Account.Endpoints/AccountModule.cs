using IAM.Modules.Account.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Account.Endpoints;

/// <summary>
/// Composition entry point for the Account module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// Registering the module makes the host require <c>ConnectionStrings:account</c> (ADR-0017).
/// </summary>
public static class AccountModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/account";

    public static IServiceCollection AddAccountModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAccountInfrastructure();
        services.AddExceptionHandler<AccountExceptionHandler>();
        services.AddSingleton<FluentValidation.IValidator<ProposeLinkBody>, ProposeLinkBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<NamingDraftBody>, NamingDraftBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<AccountReasonBody>, AccountReasonBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<ApproveNamingBody>, ApproveNamingBodyValidator>();
        return services;
    }

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // DL-089: accounts, links of existing accounts and naming rules, each decided by a second person.
        AccountEndpoints.Map(endpoints);
        return endpoints;
    }
}
