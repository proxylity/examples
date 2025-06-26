using Nager.PublicSuffix;
using Nager.PublicSuffix.RuleProviders;
using Nager.PublicSuffix.RuleProviders.CacheProviders;

namespace DnsFilterLambda;

public static class DomainUtils
{
    const string PUBLIC_SUFFIX_FILE_PATH = "public_suffix_list.dat";
    private static readonly IRuleProvider _provider = new LocalFileRuleProvider(PUBLIC_SUFFIX_FILE_PATH);
    private static readonly DomainParser _parser = new(_provider);

    public static (string? baseDomain, string? subdomain) GetDomainParts(string fqdn)
    {
        try
        {
            if (_provider.GetDomainDataStructure() == null)
            {
                _provider.BuildAsync().GetAwaiter().GetResult();
            }
            var info = _parser.Parse(fqdn);
            return (info!.RegistrableDomain, info.Subdomain ?? string.Empty);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to parse FQDN {fqdn}: {e.Message}");
            return (fqdn, string.Empty); // fallback to full domain
        }
    }
}