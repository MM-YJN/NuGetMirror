using Microsoft.Extensions.Options;

namespace NuGetMirror.Configuration;

[OptionsValidator]
internal sealed partial class MirrorOptionsValidator : IValidateOptions<MirrorOptions>
{
}
