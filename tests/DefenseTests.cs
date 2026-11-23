using ScopeCreep.Core.defense;
using ScopeCreep.Core.enums;
using ScopeCreep.Core.model;
using ScopeCreep.Core.seams;

namespace ScopeCreep.Core.Tests;



public class DefenseTests
{
    private static Battle newBattle(I_DefenseInput defense_input, I_BossBrain? boss_brain = null)
    {
        var state = new BattleState();
        state.heroes.Add(Combatant.Hero.coder());
        state.heroes.Add(Combatant.Hero.designer());

        var battle = new Battle(state) { defense_input = defense_input };
        if (boss_brain is not null)
        {
            battle.boss_brain = boss_brain;
        }

        return battle;
    }


    private static int missingHp(Battle battle) => battle.state.heroes.Sum(it => it.max_hp - it.hp);


    [Theory]
    [InlineData(0.0,   DefenseResult.PARRY)]
    [InlineData(0.05,  DefenseResult.PARRY)]
    [InlineData(-0.05, DefenseResult.PARRY)]
    [InlineData(0.06,  DefenseResult.DODGE)]
    [InlineData(-0.2,  DefenseResult.DODGE)]
    [InlineData(0.21,  DefenseResult.HIT)]
    [InlineData(-1.0,  DefenseResult.HIT)]
    public void pressIsJudgedByDistanceFromImpact(double press_offset, DefenseResult expected)
    {
        Assert.Equal(expected, DefenseRules.judge(press_offset));
    }


    [Fact]
    public void parryWindowIsMuchShorterThanDodgeWindow()
    {
        Assert.True(DefenseRules.PARRY_WINDOW * 3 <= DefenseRules.DODGE_WINDOW);
    }


    [Fact]
    public void windupStaysInRange()
    {
        var rng = new Random(1);

        for (int i = 0; i < 100; i++)
        {
            double windup = DefenseRules.rollWindup(rng);
            Assert.InRange(windup, DefenseRules.MIN_WINDUP, DefenseRules.MAX_WINDUP);
        }
    }


    [Fact]
    public async Task hitDealsTheAttackDamage()
    {
        var battle = newBattle(new FixedDefense(DefenseResult.HIT));

        await battle.runBossTurn();

        Assert.Equal(8, missingHp(battle));
    }


    [Fact]
    public async Task dodgeTakesNoDamage()
    {
        var battle = newBattle(new FixedDefense(DefenseResult.DODGE));

        await battle.runBossTurn();

        Assert.Equal(0, missingHp(battle));
    }


    [Fact]
    public async Task parryTakesNoDamageAndGainsSpiritPoints()
    {
        var battle   = newBattle(new FixedDefense(DefenseResult.PARRY));
        var coder    = battle.state.heroes[0];
        var designer = battle.state.heroes[1];
        battle.useSupportAbility(coder, new Ability.Support.Hotfix(), designer);   // 3 - 3 = 0

        await battle.runBossTurn();                                                // 0 + 1 (parry) + 3 (regen) = 4

        Assert.Equal(0, missingHp(battle));
        Assert.Equal(DefenseRules.PARRY_SP_GAIN + BattleState.SP_REGEN, battle.state.spirit_points);
    }


    [Fact]
    public async Task defendedIsRaisedForTheTarget()
    {
        var battle = newBattle(new FixedDefense(DefenseResult.DODGE));
        var events = new List<(Combatant.Hero hero, DefenseResult result)>();
        battle.defended += (hero, result) => events.Add((hero, result));

        await battle.runBossTurn();

        var single = Assert.Single(events);
        Assert.Equal(DefenseResult.DODGE, single.result);
        Assert.Contains(single.hero, battle.state.heroes);
    }


    [Fact]
    public async Task attackOnBothHeroesAsksBothAtTheSameTime()
    {
        var defense = new PendingDefense();
        var battle  = newBattle(defense, new FixedBrain(new BossAttack("Scalding Broth", 5, true)));

        var turn = battle.runBossTurn();

        Assert.Equal(2, defense.pending.Count);   // both prompts are open before either is answered

        defense.pending[battle.state.heroes[0]].SetResult(DefenseResult.PARRY);
        defense.pending[battle.state.heroes[1]].SetResult(DefenseResult.HIT);
        await turn;

        Assert.Equal(battle.state.heroes[0].max_hp,     battle.state.heroes[0].hp);
        Assert.Equal(battle.state.heroes[1].max_hp - 5, battle.state.heroes[1].hp);
    }


    private sealed class FixedDefense(DefenseResult result) : I_DefenseInput
    {
        public Task<DefenseResult> resolve(BossAttack attack, Combatant.Hero target) => Task.FromResult(result);
    }


    private sealed class PendingDefense : I_DefenseInput
    {
        public readonly Dictionary<Combatant.Hero, TaskCompletionSource<DefenseResult>> pending = new();


        public Task<DefenseResult> resolve(BossAttack attack, Combatant.Hero target)
        {
            var source = new TaskCompletionSource<DefenseResult>();
            pending[target] = source;
            return source.Task;
        }
    }


    private sealed class FixedBrain(BossAttack attack) : I_BossBrain
    {
        public BossAttack pickAttack(BattleState state) => attack;
    }
}
