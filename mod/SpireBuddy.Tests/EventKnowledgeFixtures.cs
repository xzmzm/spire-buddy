// Tiny static models let the lookup run without Godot or an installed game.
namespace MegaCrit.Sts2.Core.Models.Events
{
    public sealed class KnowledgeFlower
    {
        public static readonly int[] Costs = [5, 6, 7];
        public static readonly int[] Gold = [35, 75, 135];
        public bool IsAllowed(int hp) => hp >= 19;
        public object ReachCenter() => new Relics.KnowledgeCore();
        public string Flavor => "KnowledgeDecoy is a name in prose, not a reward reference.";
    }

    public sealed class KnowledgeCrowded
    {
        public Type[] Outcomes => [typeof(Relics.KnowledgeReward01), typeof(Relics.KnowledgeReward02),
            typeof(Relics.KnowledgeReward03), typeof(Relics.KnowledgeReward04), typeof(Relics.KnowledgeReward05),
            typeof(Relics.KnowledgeReward06), typeof(Relics.KnowledgeReward07), typeof(Relics.KnowledgeReward08),
            typeof(Relics.KnowledgeReward09), typeof(Relics.KnowledgeReward10), typeof(Relics.KnowledgeReward11),
            typeof(Relics.KnowledgeReward12), typeof(Relics.KnowledgeReward13)];
    }
}

namespace MegaCrit.Sts2.Core.Models.Relics
{
    public sealed class KnowledgeCore
    {
        public int Turns => 4;
        public int ExtraDraw => new Powers.KnowledgeDraw().Cards;
    }
    public sealed class KnowledgeDecoy { }
    public sealed class KnowledgeReward01 { }
    public sealed class KnowledgeReward02 { }
    public sealed class KnowledgeReward03 { }
    public sealed class KnowledgeReward04 { }
    public sealed class KnowledgeReward05 { }
    public sealed class KnowledgeReward06 { }
    public sealed class KnowledgeReward07 { }
    public sealed class KnowledgeReward08 { }
    public sealed class KnowledgeReward09 { }
    public sealed class KnowledgeReward10 { }
    public sealed class KnowledgeReward11 { }
    public sealed class KnowledgeReward12 { }
    public sealed class KnowledgeReward13 { }
}

namespace MegaCrit.Sts2.Core.Models.Powers
{
    public sealed class KnowledgeDraw { public int Cards => 2; }
}
