namespace Huotong.AfterSales.Api.Infrastructure;

public static class HttpClientSetup
{
    /// <summary>
    /// 注册对外的 HttpClient（微信、钉钉）。
    /// 去掉 HttpClientFactory 自带的请求日志：它在 Information 级会把完整请求地址写进日志，
    /// 而微信的 access_token / AppSecret、钉钉的 webhook 签名都在地址的查询串里，不能进日志。
    /// </summary>
    public static IServiceCollection AddOutboundHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddHttpClient("wechat").RemoveAllLoggers();
        services.AddHttpClient("dingtalk").RemoveAllLoggers();
        return services;
    }
}
