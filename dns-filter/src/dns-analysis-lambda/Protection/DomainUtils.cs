using Nager.PublicSuffix;
using Nager.PublicSuffix.RuleProviders;
using Nager.PublicSuffix.RuleProviders.CacheProviders;

namespace DnsFilterLambda;

public static class DomainUtils
{
    private static readonly DomainParser _parser = new(new CachedHttpRuleProvider(new LocalFileSystemCacheProvider(), new HttpClient()));

    public static (string? domain, string? subdomain) GetDomainParts(string fqdn)
    {
        try
        {
            var info = _parser.Parse(fqdn);
            return (info?.RegistrableDomain, info?.Subdomain);
        }
        catch
        {
            return (fqdn, null); // fallback to full domain
        }
    }
}
