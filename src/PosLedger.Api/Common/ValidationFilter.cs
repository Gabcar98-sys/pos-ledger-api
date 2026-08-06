using FluentValidation;

namespace PosLedger.Api.Common;

/// <summary>
/// Runs the FluentValidation validator registered for the request body before the handler sees it,
/// and turns failures into an RFC 9457 validation problem. Handlers therefore never start with a
/// wall of null and range checks — they can assume the body is structurally valid and only deal
/// with the rules that need the database.
/// </summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var body = context.Arguments.OfType<T>().FirstOrDefault();
        if (body is null)
        {
            return await next(context);
        }

        var result = await validator.ValidateAsync(body, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        return TypedResults.ValidationProblem(
            result.ToDictionary(),
            title: "One or more validation errors occurred.",
            instance: context.HttpContext.Request.Path);
    }
}

public static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>()
            .ProducesValidationProblem();
}
