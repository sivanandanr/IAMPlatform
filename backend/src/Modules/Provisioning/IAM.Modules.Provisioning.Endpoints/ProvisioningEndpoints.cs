using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Provisioning.Application.Items;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Provisioning.Endpoints;

/// <summary>
/// Provisioning items for the IAM team (DL-094), read only: the blocked and failed ones are their tasks. Behind
/// <c>provisioning.items.read</c>, which the service checks again. Only bounded query values are accepted.
/// </summary>
internal static class ProvisioningEndpoints
{
    public const string ItemsPrefix = ProvisioningModule.RoutePrefix + "/items";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var items = endpoints.MapGroup(ItemsPrefix).RequireAuthorization();
        items.MapGet(string.Empty, ListAsync).RequirePermission(AccessActions.ProvisioningItemsRead).WithName("Provisioning.ListItems");
        items.MapGet("/{id:guid}", GetAsync).RequirePermission(AccessActions.ProvisioningItemsRead).WithName("Provisioning.GetItem");
    }

    private static async Task<Results<Ok<ProvisioningItemPage>, ValidationProblem>> ListAsync(
        string? state, int? page, int? pageSize, IProvisioningItems items, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (state is not ("Blocked" or "Failed"))
        {
            errors["state"] = ["must be Blocked or Failed"];
        }

        if (page is < 1 or > IProvisioningItems.MaxPage)
        {
            errors["page"] = [$"must be 1 to {IProvisioningItems.MaxPage}"];
        }

        if (pageSize is < 1 or > IProvisioningItems.MaxPageSize)
        {
            errors["pageSize"] = [$"must be 1 to {IProvisioningItems.MaxPageSize}"];
        }

        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await items.ListAsync(state!, page ?? 1, pageSize ?? 50, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<ProvisioningItemView>, NotFound>> GetAsync(Guid id, IProvisioningItems items, CancellationToken cancellationToken) =>
        await items.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();
}

/// <summary>A refused read is a plain 403 with no detail about the item.</summary>
internal sealed class ProvisioningExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ProvisioningAccessDeniedException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Not allowed." },
        }).ConfigureAwait(false);
        return true;
    }
}
