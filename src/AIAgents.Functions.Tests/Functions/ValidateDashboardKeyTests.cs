using AIAgents.Functions.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIAgents.Functions.Tests.Functions;

public sealed class ValidateDashboardKeyTests
{
    private static HttpRequest CreateRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/validate-dashboard-key";
        return context.Request;
    }

    [Fact]
    public void Run_UsesFunctionAuthorization()
    {
        var method = typeof(ValidateDashboardKey).GetMethod(nameof(ValidateDashboardKey.Run));
        Assert.NotNull(method);

        var requestParameter = Assert.Single(method.GetParameters());
        var trigger = Assert.Single(requestParameter.GetCustomAttributes(typeof(HttpTriggerAttribute), false))
            as HttpTriggerAttribute;

        Assert.NotNull(trigger);
        Assert.Equal(AuthorizationLevel.Function, trigger.AuthLevel);
        Assert.Equal("validate-dashboard-key", trigger.Route);
        var methods = Assert.IsAssignableFrom<IEnumerable<string>>(trigger.Methods);
        Assert.Contains("get", methods, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_ReturnsValidPayload()
    {
        var function = new ValidateDashboardKey(NullLogger<ValidateDashboardKey>.Instance);

        var result = function.Run(CreateRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<DashboardKeyValidationResponse>(ok.Value);
        Assert.True(payload.Valid);
    }
}
