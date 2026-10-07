namespace NuGetMirror.TestKit;

public static class TestFixtures
{
    public const string NuGetOrgServiceIndex = """
    {
      "version": "3.0.0",
      "resources": [
        {
          "@id": "https://api.nuget.org/v3-flatcontainer/",
          "@type": "PackageBaseAddress/3.0.0",
          "comment": "Base URL of Azure storage where NuGet package registration info for the package is stored."
        },
        {
          "@id": "https://api.nuget.org/v3/registration5-gz-semver2/",
          "@type": "RegistrationsBaseUrl/3.6.0",
          "comment": "Base URL of Azure storage where NuGet package registration info for the package is stored, in the format of JSON. This URL includes the base URL for the semver2 storage."
        },
        {
          "@id": "https://api.nuget.org/v3/registration5-gz-semver1/",
          "@type": "RegistrationsBaseUrl/3.4.0",
          "comment": "Base URL of Azure storage where NuGet package registration info for the package is stored, in the format of JSON. The default is gz. https://api.nuget.org/v3/registration5-gz/"
        },
        {
          "@id": "https://api.nuget.org/v3/registration5/",
          "@type": "RegistrationsBaseUrl",
          "comment": "Base URL of Azure storage where NuGet package registration info for the package is stored, in the format of JSON."
        },
        {
          "@id": "https://azuresearch-usnc.nuget.org/query",
          "@type": "SearchQueryService",
          "comment": "Query endpoint of NuGet Search service."
        },
        {
          "@id": "https://azuresearch-usnc.nuget.org/query",
          "@type": "SearchQueryService/3.0.0-beta",
          "comment": "Query endpoint of NuGet Search service."
        },
        {
          "@id": "https://azuresearch-usnc.nuget.org/query",
          "@type": "SearchQueryService/3.0.0-rc",
          "comment": "Query endpoint of NuGet Search service."
        },
        {
          "@id": "https://azuresearch-usnc.nuget.org/autocomplete",
          "@type": "SearchAutocompleteService",
          "comment": "Autocomplete endpoint of NuGet Search service."
        },
        {
          "@id": "https://azuresearch-usnc.nuget.org/autocomplete",
          "@type": "SearchAutocompleteService/3.0.0-beta",
          "comment": "Autocomplete endpoint of NuGet Search service."
        },
        {
          "@id": "https://azuresearch-usnc.nuget.org/autocomplete",
          "@type": "SearchAutocompleteService/3.0.0-rc",
          "comment": "Autocomplete endpoint of NuGet Search service."
        },
        {
          "@id": "https://api.nuget.org/v3/catalog0/index.json",
          "@type": "Catalog/3.0.0",
          "comment": "Index of the NuGet package source."
        },
        {
          "@id": "https://api.nuget.org/v3/vulnerabilities/index.json",
          "@type": "VulnerabilityInfo/6.7.0",
          "comment": "The endpoint for discovering information about vulnerabilities of packages in this package source."
        },
        {
          "@id": "https://api.nuget.org/v3-index/repository-signatures/4.7.0/index.json",
          "@type": "RepositorySignatures/4.7.0",
          "comment": "The endpoint for discovering information about this package source's repository signatures."
        },
        {
          "@id": "https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json",
          "@type": "RepositorySignatures/5.0.0",
          "comment": "The endpoint for discovering information about this package source's repository signatures."
        },
        {
          "@id": "https://globalcdn.nuget.org/v3-flatcontainer/{lower_id}/{lower_version}/readme",
          "@type": "ReadmeUriTemplate/6.13.0",
          "comment": "URI template used by NuGet Client to construct a URL for downloading a package's README."
        }
      ]
    }
    """;

    public const string RewrittenUpstreamIndex = """
    {
      "version": "3.0.0",
      "resources": [
        {
          "@id": "https://corp.example.com/x/flat/",
          "@type": "PackageBaseAddress/3.0.0",
          "comment": "Custom flat container base."
        },
        {
          "@id": "https://corp.example.com/x/reg-gz2/",
          "@type": "RegistrationsBaseUrl/3.6.0",
          "comment": "Custom registration semver2 base."
        },
        {
          "@id": "https://corp.example.com/x/reg-gz1/",
          "@type": "RegistrationsBaseUrl/3.4.0",
          "comment": "Custom registration gz semver1 base."
        },
        {
          "@id": "https://corp.example.com/x/reg/",
          "@type": "RegistrationsBaseUrl",
          "comment": "Custom registration base."
        },
        {
          "@id": "https://corp.example.com/search",
          "@type": "SearchQueryService",
          "comment": "Search endpoint."
        },
        {
          "@id": "https://cdn.example.com/v3-flatcontainer/{lower_id}/{lower_version}/readme",
          "@type": "ReadmeUriTemplate/6.13.0",
          "comment": "URI template used by NuGet Client to construct a URL for downloading a package's README."
        }
      ]
    }
    """;
}
