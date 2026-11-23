using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using ScopeCreep.Core.defense;
using ScopeCreep.Core.enums;
using ScopeCreep.Core.model;
using ScopeCreep.Core.seams;

namespace ScopeCreep.Game.defense;



/**
 *  The dodge & parry quick time event (Mario & Luigi style).
 *  The boss throws a projectile at a hero, and the player presses that hero's key as it lands:
 *  right on the impact parries, close to it dodges. Too early, too late, no press, or the other
 *  hero's key gets the targeted hero hit. Each hero's key is shown above them the whole fight.
 */
public partial class DefenseLayer : Control, I_DefenseInput
{
    /** How long the hit / dodge / parry animation plays after the defense is decided. */
    public const double OUTCOME_TIME = 0.6;

    private const float  PROJECTILE_SIZE = 28f;
    private const float  JUMP_HEIGHT     = 70f;
    private const double JUMP_TIME       = 0.15;
    private const double HIT_STOP        = 0.09;
    private const float  KNOCKBACK       = 36f;
    private const float  SHAKE_ON_HIT    = 22f;
    private const float  SHAKE_ON_PARRY  = 7f;
    private const float  SHAKE_DECAY     = 70f;   // pixels per second

    private static readonly Color projectile_color = new("#8b5a2b");
    private static readonly Color dim_badge_color  = new(1, 1, 1, 0.45f);
    private static readonly Color hurt_color       = new(1, 0.25f, 0.25f);
    private static readonly Color white_hot        = new(3, 3, 3);

    private readonly List<HeroSlot> slots  = [];
    private readonly List<Qte>      active = [];

    private Control   boss_sprite  = null!;
    private Control   shake_target = null!;
    private Label     hint         = null!;
    private ColorRect screen_flash = null!;
    private Tween?    boss_motion;
    private float     shake_strength;
    private Vector2   shake_rest;


    /** One hero on screen: its sprite, its key and the badge that shows the key. */
    private sealed class HeroSlot
    {
        public required Combatant.Hero hero;
        public required Control        sprite;
        public required string         action;
        public required Label          badge;

        public Tween?  motion;
        public Vector2 rest_position;
    }


    /** One open quick time event: a projectile flying at one hero. */
    private sealed class Qte
    {
        public required HeroSlot   slot;
        public required BossAttack attack;
        public required ulong      impact_usec;

        public DefenseResult? result;
    }


    /**
     *  Build the overlay. Call after adding the layer to the tree.
     *  shake_target is the node moved by the screen shake when a hero is hit.
     *  Each hero comes with the sprite to animate, the input action that defends it and the key used
     *  when that action isn't set in Project Settings > Input Map.
     */
    public void setup(
        Control boss_sprite,
        Control shake_target,
        IEnumerable<(Combatant.Hero hero, Control sprite, string action, Key default_key)> heroes)
    {
        this.boss_sprite  = boss_sprite;
        this.shake_target = shake_target;

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        screen_flash = new ColorRect
        {
            Color       = hurt_color with { A = 0 },
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(screen_flash);
        screen_flash.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        hint = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter         = MouseFilterEnum.Ignore,
            Visible             = false,
        };
        addOutline(hint, 26);
        AddChild(hint);
        hint.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        hint.OffsetTop    = 56;
        hint.OffsetBottom = 56;

        foreach (var (hero, sprite, action, default_key) in heroes)
        {
            ensureAction(action, default_key);

            var badge = makeBadge(keyName(action));
            AddChild(badge);
            slots.Add(new HeroSlot { hero = hero, sprite = sprite, action = action, badge = badge });
        }
    }


    /**
     *  The boss winds up (random length), then throws a fast projectile (always the same flight time).
     *  Returns as soon as the defense is decided, so the damage lands on the impact; the outcome
     *  animation keeps playing for OUTCOME_TIME after that.
     */
    public async Task<DefenseResult> resolve(BossAttack attack, Combatant.Hero target)
    {
        var    slot = slots.First(it => it.hero == target);
        double tell = DefenseRules.rollTell(Random.Shared);

        var qte = new Qte
        {
            slot        = slot,
            attack      = attack,
            impact_usec = usecFromNow(tell + DefenseRules.FLIGHT_TIME),
        };
        active.Add(qte);
        refreshHint();

        windUp(tell);
        await wait(tell);

        qte.impact_usec = usecFromNow(DefenseRules.FLIGHT_TIME);   // measured from the actual throw
        var projectile  = launchProjectile(slot);
        await wait(DefenseRules.FLIGHT_TIME);

        if (qte.result is null)
        {
            await wait(DefenseRules.DODGE_WINDOW);   // a late press can still dodge
        }

        active.Remove(qte);
        refreshHint();

        var result = qte.result ?? DefenseResult.HIT;
        playOutcome(slot, projectile, result);

        return result;
    }


    /**
     *  Pop a big damage number over a hero. Called by the scene when the battle reports the damage.
     */
    public void showDamage(Combatant.Hero hero, int amount)
    {
        var slot = slots.FirstOrDefault(it => it.hero == hero);
        if (slot is null) return;

        floatText(slot, $"-{amount}", hurt_color, 44);
    }


    public override void _UnhandledInput(InputEvent input_event)
    {
        if (active.Count == 0) return;

        foreach (var slot in slots)
        {
            if (slot.hero.is_down || !input_event.IsActionPressed(slot.action)) continue;

            GetViewport().SetInputAsHandled();
            onKeyPressed(slot);
        }
    }


    public override void _Process(double delta)
    {
        float pulse = 1.2f + 0.1f * Mathf.Sin(Time.GetTicksMsec() / 80f);

        foreach (var slot in slots)
        {
            bool is_targeted = active.Any(it => it.slot == slot && it.result is null);
            var  badge       = slot.badge;
            var  top_center  = slot.sprite.GlobalPosition + new Vector2(slot.sprite.Size.X / 2, 0);

            badge.Visible        = !slot.hero.is_down;
            badge.Modulate       = is_targeted ? Colors.Yellow : dim_badge_color;
            badge.PivotOffset    = badge.Size / 2;
            badge.Scale          = is_targeted ? Vector2.One * pulse : Vector2.One;
            badge.GlobalPosition = top_center - new Vector2(badge.Size.X / 2, badge.Size.Y + 12);
        }

        updateShake((float)delta);
    }


    /**
     *  The first press for a hero locks in the result, so mashing doesn't work.
     *  Pressing the key of a hero that isn't under attack wastes the defense of the one that is.
     */
    private void onKeyPressed(HeroSlot slot)
    {
        var own = active.FirstOrDefault(it => it.slot == slot && it.result is null);

        if (own is not null)
        {
            double offset = ((long)Time.GetTicksUsec() - (long)own.impact_usec) / 1_000_000.0;
            own.result = DefenseRules.judge(offset);
            playPress(slot, own.result.Value);
        }
        else if (active.All(it => it.slot != slot))
        {
            jump(slot);
            foreach (var qte in active.Where(it => it.result is null))
            {
                qte.result = DefenseResult.HIT;
            }
        }

        refreshHint();
    }


    private void refreshHint()
    {
        var waiting = active.Where(it => it.result is null).ToList();

        hint.Visible = waiting.Count > 0;
        if (waiting.Count == 0) return;

        string keys    = string.Join(" + ", waiting.Select(it => $"[{it.slot.badge.Text}]"));
        string targets = waiting.Count == 1 ? waiting[0].slot.hero.name : "everyone";

        hint.Text = $"{waiting[0].attack.name} → {targets}!\nPress {keys} as it lands · perfect timing = PARRY";
    }


    /**
     *  The boss squashes down while it winds up, then stretches as it throws.
     */
    private void windUp(double tell)
    {
        boss_motion?.Kill();
        boss_sprite.PivotOffset = new Vector2(boss_sprite.Size.X / 2, boss_sprite.Size.Y);

        boss_motion = boss_sprite.CreateTween();
        boss_motion.TweenProperty(boss_sprite, "scale", new Vector2(1.12f, 0.85f), tell)
                   .SetTrans(Tween.TransitionType.Quad)
                   .SetEase(Tween.EaseType.Out);
        boss_motion.TweenProperty(boss_sprite, "scale", new Vector2(0.9f, 1.15f), 0.06);
        boss_motion.TweenProperty(boss_sprite, "scale", Vector2.One, 0.2)
                   .SetTrans(Tween.TransitionType.Back)
                   .SetEase(Tween.EaseType.Out);
    }


    /**
     *  The projectile speeds up as it flies, so it slams into the hero.
     */
    private Panel launchProjectile(HeroSlot slot)
    {
        var style = new StyleBoxFlat { BgColor = projectile_color };
        style.SetCornerRadiusAll((int)(PROJECTILE_SIZE / 2));

        var projectile = new Panel
        {
            Size        = Vector2.One * PROJECTILE_SIZE,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        projectile.AddThemeStyleboxOverride("panel", style);
        AddChild(projectile);

        projectile.GlobalPosition = projectileSpot(boss_sprite);
        projectile.CreateTween()
                  .TweenProperty(projectile, "global_position", projectileSpot(slot.sprite), DefenseRules.FLIGHT_TIME)
                  .SetTrans(Tween.TransitionType.Quad)
                  .SetEase(Tween.EaseType.In);

        return projectile;
    }


    /**
     *  What the projectile does once the window closes.
     */
    private void playOutcome(HeroSlot slot, Panel projectile, DefenseResult result)
    {
        switch (result)
        {
            case DefenseResult.HIT:
            {
                takeHit(slot, projectile);
                break;
            }
            case DefenseResult.DODGE:
            {
                var direction = (projectile.GlobalPosition - projectileSpot(boss_sprite)).Normalized();
                var flyby     = projectile.CreateTween().SetParallel();
                flyby.TweenProperty(projectile, "global_position", projectile.GlobalPosition + direction * 160, 0.3);
                flyby.TweenProperty(projectile, "modulate:a", 0.0, 0.3);
                flyby.Chain().TweenCallback(Callable.From(projectile.QueueFree));
                floatText(slot, "DODGE", Colors.SkyBlue);
                break;
            }
            case DefenseResult.PARRY:
            {
                var bounce = projectile.CreateTween();
                bounce.TweenProperty(projectile, "global_position", projectileSpot(boss_sprite), 0.25)
                      .SetTrans(Tween.TransitionType.Quad)
                      .SetEase(Tween.EaseType.In);
                bounce.TweenCallback(Callable.From(() =>
                {
                    flash(boss_sprite);
                    shake(SHAKE_ON_PARRY);
                    projectile.QueueFree();
                }));
                floatText(slot, $"PARRY! +{DefenseRules.PARRY_SP_GAIN} SP", Colors.Gold);
                break;
            }
            default:
                throw new UnreachableException();
        }
    }


    /**
     *  What the hero does the moment the key is pressed: swing on a parry, jump otherwise.
     */
    private void playPress(HeroSlot slot, DefenseResult result)
    {
        if (result == DefenseResult.PARRY)
        {
            swing(slot);
        }
        else
        {
            jump(slot);
        }
    }


    private void jump(HeroSlot slot)
    {
        var motion = restartMotion(slot);
        float rest_y = slot.rest_position.Y;

        motion.TweenProperty(slot.sprite, "position:y", rest_y - JUMP_HEIGHT, JUMP_TIME)
              .SetTrans(Tween.TransitionType.Quad)
              .SetEase(Tween.EaseType.Out);
        motion.TweenProperty(slot.sprite, "position:y", rest_y, JUMP_TIME)
              .SetTrans(Tween.TransitionType.Quad)
              .SetEase(Tween.EaseType.In);
    }


    private void swing(HeroSlot slot)
    {
        var motion = restartMotion(slot).SetParallel();
        slot.sprite.PivotOffset = slot.sprite.Size / 2;

        motion.TweenProperty(slot.sprite, "scale", Vector2.One * 1.2f, 0.08);
        motion.TweenProperty(slot.sprite, "modulate", new Color(2, 2, 2), 0.08);
        motion.Chain().TweenProperty(slot.sprite, "scale", Vector2.One, 0.15);
        motion.TweenProperty(slot.sprite, "modulate", Colors.White, 0.15);
    }


    /**
     *  Hit-stop (everything holds for a beat with the hero flashing white), then the projectile bursts,
     *  the hero is knocked back and flashes red, the screen shakes and flashes red.
     */
    private void takeHit(HeroSlot slot, Panel projectile)
    {
        var   motion = restartMotion(slot);
        float rest_x = slot.rest_position.X;
        float away   = Mathf.Sign(slot.sprite.GlobalPosition.X - boss_sprite.GlobalPosition.X);

        slot.sprite.Modulate = white_hot;
        motion.TweenInterval(HIT_STOP);
        motion.TweenCallback(Callable.From(() =>
        {
            slot.sprite.Modulate = hurt_color;
            shake(SHAKE_ON_HIT);
            flashScreen();
        }));
        motion.TweenProperty(slot.sprite, "position:x", rest_x + away * KNOCKBACK, 0.07)
              .SetTrans(Tween.TransitionType.Expo)
              .SetEase(Tween.EaseType.Out);
        motion.TweenProperty(slot.sprite, "position:x", rest_x - away * 6, 0.05);
        motion.TweenProperty(slot.sprite, "position:x", rest_x + away * 4, 0.05);
        motion.TweenProperty(slot.sprite, "position:x", rest_x, 0.25)
              .SetTrans(Tween.TransitionType.Quad)
              .SetEase(Tween.EaseType.Out);
        motion.Parallel().TweenProperty(slot.sprite, "modulate", Colors.White, 0.3);

        projectile.PivotOffset = projectile.Size / 2;
        var burst = projectile.CreateTween();
        burst.TweenInterval(HIT_STOP);
        burst.TweenProperty(projectile, "scale", Vector2.One * 2.5f, 0.12);
        burst.Parallel().TweenProperty(projectile, "modulate:a", 0.0, 0.12);
        burst.TweenCallback(Callable.From(projectile.QueueFree));
    }


    private void flashScreen()
    {
        screen_flash.Color = hurt_color with { A = 0.4f };
        screen_flash.CreateTween().TweenProperty(screen_flash, "color:a", 0.0, 0.35);
    }


    private void shake(float strength)
    {
        if (shake_strength <= 0)
        {
            shake_rest = shake_target.Position;
        }

        shake_strength = Mathf.Max(shake_strength, strength);
    }


    private void updateShake(float delta)
    {
        if (shake_strength <= 0) return;

        shake_strength = Mathf.MoveToward(shake_strength, 0, SHAKE_DECAY * delta);

        var offset = new Vector2((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0)) * shake_strength;
        shake_target.Position = shake_strength > 0 ? shake_rest + offset : shake_rest;
    }


    /**
     *  Stop the hero's current animation (snapping back to rest) and start a new one.
     */
    private static Tween restartMotion(HeroSlot slot)
    {
        if (slot.motion is not null && slot.motion.IsValid())
        {
            slot.motion.Kill();
            slot.sprite.Position = slot.rest_position;
            slot.sprite.Scale    = Vector2.One;
            slot.sprite.Modulate = Colors.White;
        }
        else
        {
            slot.rest_position = slot.sprite.Position;
        }

        slot.motion = slot.sprite.CreateTween();
        return slot.motion;
    }


    private static void flash(Control target)
    {
        var tween = target.CreateTween();
        tween.TweenProperty(target, "modulate", new Color(2, 2, 2), 0.06);
        tween.TweenProperty(target, "modulate", Colors.White, 0.2);
    }


    private void floatText(HeroSlot slot, string text, Color color, int font_size = 28)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        addOutline(label, font_size);
        label.AddThemeColorOverride("font_color", color);
        AddChild(label);

        var size   = label.GetCombinedMinimumSize();
        var center = slot.sprite.GlobalPosition + slot.sprite.Size / 2;
        label.GlobalPosition = center - size / 2;
        label.PivotOffset    = size / 2;
        label.Scale          = Vector2.One * 1.6f;

        var rise = label.CreateTween().SetParallel();
        rise.TweenProperty(label, "scale", Vector2.One, 0.15).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        rise.TweenProperty(label, "position:y", label.Position.Y - 50, 0.8);
        rise.TweenProperty(label, "modulate:a", 0.0, 0.8).SetDelay(0.3);
        rise.Chain().TweenCallback(Callable.From(label.QueueFree));
    }


    private static Label makeBadge(string key)
    {
        var style = new StyleBoxFlat
        {
            BgColor     = new Color(0.1f, 0.1f, 0.12f, 0.9f),
            BorderColor = Colors.White,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(6);

        var badge = new Label
        {
            Text                = key,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter         = MouseFilterEnum.Ignore,
        };
        badge.AddThemeStyleboxOverride("normal", style);
        badge.AddThemeFontSizeOverride("font_size", 22);

        return badge;
    }


    private static void addOutline(Label label, int font_size)
    {
        label.AddThemeFontSizeOverride("font_size", font_size);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 6);
    }


    private static void ensureAction(string action, Key default_key)
    {
        if (InputMap.HasAction(action)) return;

        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = default_key });
    }


    /** The key bound to an input action, as text ("F"), or the action name if it has no key. */
    private static string keyName(string action)
    {
        var key = InputMap.ActionGetEvents(action).OfType<InputEventKey>().FirstOrDefault();
        return key?.AsTextPhysicalKeycode() ?? action;
    }


    /** Where the projectile sits when centered on a control. */
    private static Vector2 projectileSpot(Control control) =>
        control.GlobalPosition + control.Size / 2 - Vector2.One * PROJECTILE_SIZE / 2;


    private static ulong usecFromNow(double seconds) => Time.GetTicksUsec() + (ulong)(seconds * 1_000_000);


    private SignalAwaiter wait(double seconds) =>
        ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
}
