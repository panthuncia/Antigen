using Antigen.SDK.Analyzers;
using Antigen.SDK.Caches;
using Antigen.SDK.Topics;
using Antigen.Skyrim.Caches;
using Mutagen.Bethesda.Skyrim;

namespace Antigen.Skyrim.Record.Npc;

public class HasAmmoAnalyzer : IContextualRecordAnalyzer<INpcGetter>, IUsesCaches
{
    public IEnumerable<Type> Caches => [typeof(ILeveledListCache)];

    public static readonly TopicDefinition<IWeaponGetter, string> MissingAmmo = MutagenTopicBuilder.FromDiscussion(
            405,
            "Npc is missing ammo",
            Severity.Error)
        .WithFormatting<IWeaponGetter, string>("Npc has ranged weapon {0} but no {1}");

    public IEnumerable<TopicDefinition> Topics { get; } = [MissingAmmo];

    public void AnalyzeRecord(ContextualRecordAnalyzerParams<INpcGetter> param)
    {
        var npc = param.Record;
        if (npc.Items is null) return;

        IWeaponGetter? crossbow = null;
        IWeaponGetter? bow = null;
        IAmmunitionGetter? arrow = null;
        IAmmunitionGetter? bolt = null;
        // What each leveled item holds is worked out once for the load order, rather than searched again for each NPC.
        var leveledLists = param.ResolveCache<ILeveledListCache>();
        foreach (var entry in npc.Items)
        {
            var item = entry.Item.Item.TryResolve(param.LinkCache);

            IWeaponGetter? weapon;
            IAmmunitionGetter? ammo;
            if (item is ILeveledItemGetter leveled && leveledLists.TryGetRanged(leveled.FormKey, out var rangedWeapon, out var ammunition))
            {
                weapon = rangedWeapon;
                ammo = ammunition;
            }
            else
            {
                weapon = item?.FindItem<IWeaponGetter>(param.LinkCache, LeveledListCache.IsRanged);
                ammo = item?.FindItem<IAmmunitionGetter>(param.LinkCache, _ => true);
            }

            if (weapon != null)
            {
                if (weapon.Data is null) break;

                if (weapon.Data.AnimationType == WeaponAnimationType.Bow)
                {
                    bow = weapon;
                }
                else if (weapon.Data.AnimationType == WeaponAnimationType.Crossbow)
                {
                    crossbow = weapon;
                }
            }

            if (ammo != null)
            {
                if (ammo.Flags.HasFlag(Ammunition.Flag.NonBolt))
                {
                    arrow = ammo;
                }
                else
                {
                    bolt = ammo;
                }
            }
        }

        if (crossbow is not null && bolt is null)
        {
            param.AddTopic(MissingAmmo.Format(crossbow, "bolts"));
        }

        if (bow is not null && arrow is null)
        {
            param.AddTopic(MissingAmmo.Format(bow, "arrows"));
        }
    }

    public IEnumerable<Func<INpcGetter, object?>> FieldsOfInterest()
    {
        yield return x => x.Items;
    }
}
