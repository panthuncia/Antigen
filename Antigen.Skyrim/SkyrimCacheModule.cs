using Autofac;
using Antigen.SDK;
using Antigen.SDK.Caches;
using Antigen.Skyrim.Caches;
using Noggog.Autofac;
using Module = Autofac.Module;

namespace Antigen.Skyrim;

public class SkyrimCacheModule : Module, ICacheModule
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterAssemblyTypes(typeof(VoiceTypeAssetLookupProvider).Assembly)
            .InNamespacesOf(typeof(VoiceTypeAssetLookupProvider))
            // The constructors only: the caches they make, and what those hold, are made by them.
            .Where(t => typeof(ICacheConstructor).IsAssignableFrom(t))
            .AsSelf()
            .AsImplementedInterfaces()
            .SingleInstance();
    }
}
