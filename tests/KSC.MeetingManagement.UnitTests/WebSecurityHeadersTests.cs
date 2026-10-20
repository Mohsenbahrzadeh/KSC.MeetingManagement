using KSC.MeetingManagement.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace KSC.MeetingManagement.UnitTests;

public class WebSecurityHeadersTests
{
    private sealed class RunningResponseFeature : HttpResponseFeature
    {
        private readonly List<Func<Task>> _callbacks = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add(() => callback(state));

        public async Task StartAsync()
        {
            foreach (var callback in _callbacks)
            {
                await callback();
            }
        }
    }

    private static async Task<DefaultHttpContext> RunAsync()
    {
        var feature = new RunningResponseFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(feature);
        await new WebSecurityHeadersMiddleware(_ => feature.StartAsync()).InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task Responses_carry_the_security_headers_and_a_policy_that_allows_only_this_site_for_scripts_and_fonts()
    {
        var context = await RunAsync();

        var headers = context.Response.Headers;
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        var csp = headers["Content-Security-Policy"].ToString();
        Assert.Contains($"script-src 'self' 'nonce-{context.Items[WebSecurityHeadersMiddleware.NonceKey]}';", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", csp.Replace("ws:", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("camera=(self)", headers["Permissions-Policy"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_response_gets_a_new_random_nonce()
    {
        var a = await RunAsync();
        var b = await RunAsync();

        Assert.NotEqual(a.Items[WebSecurityHeadersMiddleware.NonceKey], b.Items[WebSecurityHeadersMiddleware.NonceKey]);
    }
}
