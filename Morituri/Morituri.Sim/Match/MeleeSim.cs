using Morituri.Sim.Core;
using Morituri.Sim.Data;

namespace Morituri.Sim.Match;

/// <summary>
/// 다대다 난투 시뮬레이터 (패싸움 전용). **1v1 MatchSim과 완전 분리** — 전투 매트릭스에 무영향.
/// 기존 sim의 자산(FighterRuntime·FighterState FSM·CombatMath·무기/특성 데이터)을 재사용해
/// 1v1 전투의 표현 요소를 모두 담는다: 접근→윈드업→액티브→후딜 FSM, 스태미나, 거리조절(카이터/인파이터),
/// 특성(출혈·크리·처형자/광폭화/일격필살/좀비·거인 사거리·숨고르기), 가드. 정밀 밸런스는 무관(난투).
/// 결정론(단일 SimRandom).
/// </summary>
public sealed class MeleeSim
{
    private const float Dt = 1f / 60f;
    private const float MaxTime = 55f;
    private const float ArenaR = 9.5f;
    private const float FrameSampleSec = 1f / 20f;

    public sealed record Unit(string Name, int Team, string Weapon);
    public sealed record FrameUnit(float X, float Y, float HpPct, float StamPct, string State, int Facing, bool Heavy, int Bleed, bool Hit, bool Dead);
    public sealed record Frame(float T, FrameUnit[] Units);
    public sealed record Outcome(string Name, int Team, float DamageDealt, int Kills, bool Survived, float MinHpPct);
    public sealed record MeleeResult(int WinningTeam, string Reason, float DurationSec, List<Outcome> Outcomes);

    private sealed class M
    {
        public required FighterRuntime Rt; public required int Team;
        public float Dealt; public int Kills; public float MinHpPct = 1f;
        public int Facing = 1; public float NextReady; public int TargetIdx = -1;
        public float RetreatUntil;                 // 카이터: 공격 후 후퇴 창
        public float HitFlashFrame;                // 이번 프레임 피격 표시
        public bool FirstHitDone;
        public bool Kiter;                         // 창·채찍(사거리 유지)
        public bool Dead => Rt.Hp <= 0f && Rt.State == FighterState.Down;
        public FighterState S { get => Rt.State; set => Rt.State = value; }
    }

    public (MeleeResult Result, List<Frame> Frames, List<Unit> Units) Run(
        IReadOnlyList<(FighterDef Def, int Team)> roster, ulong seed)
    {
        var rng = new SimRandom(seed);
        var us = new List<M>();
        int[] tc = { roster.Count(r => r.Team == 0), roster.Count(r => r.Team == 1) };
        int[] placed = { 0, 0 }; int idx = 0;
        foreach (var (def, team) in roster)
        {
            int n = tc[team]; int i = placed[team]++;
            float side = team == 0 ? -1f : 1f;
            float y = n <= 1 ? 0f : (i / (float)(n - 1) - 0.5f) * 6.5f;
            var rt = MakeRuntime(idx++, def, new Vec2(side * 5.2f, y));
            us.Add(new M { Rt = rt, Team = team, Facing = team == 0 ? 1 : -1,
                Kiter = rt.Weapon.PostAttackRetreatSec > 0f });
        }

        var meta = us.Select(u => new Unit(u.Rt.Def.Name, u.Team, u.Rt.Weapon.Id.Replace("WPN_", ""))).ToList();
        var frames = new List<Frame>();
        float t = 0f, nextFrame = 0f; string reason = "Judgement";

        for (; t < MaxTime; t += Dt)
        {
            foreach (var u in us) { u.HitFlashFrame = MathF.Max(0f, u.HitFlashFrame - Dt); TickBleed(u, t); }

            foreach (var u in us)
            {
                if (u.Dead) continue;
                // 스태미나 회복
                u.Rt.Stamina = MathF.Min(u.Rt.StaminaMax, u.Rt.Stamina + 16f * Dt);

                if (u.TargetIdx < 0 || us[u.TargetIdx].Dead)
                {
                    float best = float.MaxValue; u.TargetIdx = -1;
                    for (int j = 0; j < us.Count; j++)
                    {
                        if (us[j].Dead || us[j].Team == u.Team) continue;
                        float d = Vec2.Dist(u.Rt.Pos, us[j].Rt.Pos);
                        if (d < best) { best = d; u.TargetIdx = j; }
                    }
                }
                if (u.TargetIdx < 0) { u.S = FighterState.Idle; continue; }
                var tgt = us[u.TargetIdx];
                u.Facing = tgt.Rt.Pos.X >= u.Rt.Pos.X ? 1 : -1;
                float dist = Vec2.Dist(u.Rt.Pos, tgt.Rt.Pos);
                float reach = u.Rt.EffRange + 0.4f;
                float keep = u.Kiter ? u.Rt.EffRange * u.Rt.Weapon.EngageRangeRatio : reach;   // 카이터=사거리 끝 유지

                // 커밋 상태 진행
                u.Rt.StateTimer -= Dt;
                if (u.S is FighterState.Windup or FighterState.Active or FighterState.Recovery
                        or FighterState.Stagger or FighterState.HitStun or FighterState.Guard)
                {
                    if (u.Rt.StateTimer > 0f) continue;
                    switch (u.S)
                    {
                        case FighterState.Windup:
                            u.S = FighterState.Active; u.Rt.StateTimer = 0.10f;
                            if (dist <= reach + 0.7f) Hit(u, tgt, rng, t);
                            break;
                        case FighterState.Active:
                            u.S = FighterState.Recovery; u.Rt.StateTimer = u.Rt.Weapon.RecoverySec;
                            if (u.Kiter) u.RetreatUntil = t + u.Rt.Weapon.PostAttackRetreatSec;   // 찌르고 빠짐
                            break;
                        default: u.S = FighterState.Idle; u.Rt.StateTimer = 0f; break;
                    }
                    continue;
                }

                // 카이터 공격 후 후퇴
                if (t < u.RetreatUntil) { u.S = FighterState.Move; MoveAway(u, tgt, us); continue; }

                if (dist > keep + 0.3f) { u.S = FighterState.Move; MoveToward(u, tgt, us, dist, keep); }
                else if (u.Kiter && dist < keep - 0.6f) { u.S = FighterState.Move; MoveAway(u, tgt, us); }   // 너무 붙었으면 벌림
                else if (t >= u.NextReady && u.Rt.Stamina >= 12f && dist <= reach + 0.3f)
                {
                    // 가끔 상대 윈드업을 보고 가드(방어형·랜덤)
                    if (tgt.S == FighterState.Windup && rng.Roll(0.18f))
                    { u.S = FighterState.Guard; u.Rt.StateTimer = 0.35f; }
                    else
                    {
                        bool heavy = u.Rt.Weapon.HeavyBias > 0f && u.Rt.Stamina >= 20f && rng.Roll(0.6f);
                        u.S = FighterState.Windup;
                        u.Rt.MotionKindNow = heavy ? MotionKind.Heavy : MotionKind.Light;
                        u.Rt.StateTimer = (0.26f + (heavy ? 0.22f : 0f)) / u.Rt.Weapon.MotionSpeed;
                        u.Rt.Stamina -= heavy ? 20f : 12f;
                        u.NextReady = t + (0.35f + 0.85f / (0.4f + u.Rt.Def.Stats.Aspd / 70f));
                    }
                }
                else u.S = FighterState.Idle;
            }

            if (t >= nextFrame) { frames.Add(Snap(us, t)); nextFrame += FrameSampleSec; }
            int a0 = us.Count(u => u.Team == 0 && !u.Dead), a1 = us.Count(u => u.Team == 1 && !u.Dead);
            if (a0 == 0 || a1 == 0) { reason = "KO"; t += Dt; break; }
        }
        frames.Add(Snap(us, t));

        int al0 = us.Count(u => u.Team == 0 && !u.Dead), al1 = us.Count(u => u.Team == 1 && !u.Dead);
        int winner = al0 == al1 ? Judge(us) : (al0 > al1 ? 0 : 1);
        var outcomes = us.Select(u => new Outcome(u.Rt.Def.Name, u.Team,
            MathF.Round(u.Dealt), u.Kills, !u.Dead, u.MinHpPct)).ToList();
        return (new MeleeResult(winner, reason, t, outcomes), frames, meta);
    }

    private void MoveToward(M u, M tgt, List<M> us, float dist, float keep)
    {
        float mv = u.Rt.MoveSpeed * Dt;
        var dir = (tgt.Rt.Pos - u.Rt.Pos).Normalized();
        u.Rt.Pos += dir * mv + Repel(u, us) * Dt;
        Clamp(u);
    }
    private void MoveAway(M u, M tgt, List<M> us)
    {
        float mv = u.Rt.MoveSpeed * 0.9f * Dt;
        var dir = (u.Rt.Pos - tgt.Rt.Pos).Normalized();
        u.Rt.Pos += dir * mv + Repel(u, us) * Dt;
        Clamp(u);
    }
    private static Vec2 Repel(M u, List<M> us)
    {
        var r = new Vec2(0, 0);
        foreach (var a in us) if (a != u && !a.Dead && Vec2.Dist(a.Rt.Pos, u.Rt.Pos) < 1.1f)
            r += (u.Rt.Pos - a.Rt.Pos).Normalized() * 0.5f;
        return r;
    }
    private static void Clamp(M u) { if (u.Rt.Pos.Length > ArenaR) u.Rt.Pos = u.Rt.Pos.Normalized() * ArenaR; }

    private static void TickBleed(M u, float t)
    {
        if (u.Rt.BleedStacks <= 0 || u.Dead) return;
        if (t >= u.Rt.BleedExpiry) { u.Rt.BleedStacks = 0; return; }
        u.Rt.Hp -= u.Rt.BleedStacks * u.Rt.BleedDps * Dt;
        u.MinHpPct = MathF.Min(u.MinHpPct, MathF.Max(0f, u.Rt.Hp / u.Rt.HpMax));
        if (u.Rt.Hp <= 0f && u.S != FighterState.Down) { u.S = FighterState.Down; u.Rt.StateTimer = 999f; }
    }

    private static void Hit(M atk, M tgt, SimRandom rng, float t)
    {
        if (tgt.Dead) return;
        // 가드 중이면 대폭 경감
        float guard = tgt.S == FighterState.Guard ? 0.35f : 1f;
        float dmg = atk.Rt.Weapon.BaseDamage * atk.Rt.Weapon.HitCount
                  * (0.55f + atk.Rt.Def.Stats.Atk / 130f)
                  / (0.55f + tgt.Rt.Def.Stats.Def / 130f)
                  * (0.85f + rng.NextFloat01() * 0.3f) * guard;
        // 특성 표현(전투 배율) — MatchSim과 같은 계열(밸런스 무관)
        bool crit = false;
        if (atk.Rt.Has(TraitTable.CatchBreath) && atk.Rt.Stamina >= atk.Rt.StaminaMax * 0.8f) crit = true;
        if (atk.Rt.Has(TraitTable.Executioner) && tgt.Rt.Hp <= tgt.Rt.HpMax * 0.30f) dmg *= 1.5f;
        if (atk.Rt.Has(TraitTable.Berserk) && atk.Rt.Hp <= atk.Rt.HpMax * 0.35f) dmg *= 1.3f;
        if (atk.Rt.Has(TraitTable.Assassin) && rng.Roll(0.04f)) { dmg *= 2.4f; crit = true; }
        if (!atk.FirstHitDone && atk.Rt.Has(TraitTable.FirstBlood)) { dmg *= 1.25f; atk.FirstHitDone = true; }
        if (crit) dmg *= 1.5f;
        dmg *= tgt.Rt.DamageTakenMult;   // 유리몸/질긴가죽 등

        tgt.Rt.Hp -= dmg; atk.Dealt += dmg; atk.FirstHitDone = true;
        tgt.HitFlashFrame = crit ? 0.28f : 0.16f;
        tgt.MinHpPct = MathF.Min(tgt.MinHpPct, MathF.Max(0f, tgt.Rt.Hp / tgt.Rt.HpMax));
        // 출혈(도끼) — 스택 누적
        if (atk.Rt.Weapon.BleedDps > 0f && guard > 0.5f)
        { tgt.Rt.BleedStacks = Math.Min(3, tgt.Rt.BleedStacks + 1); tgt.Rt.BleedDps = atk.Rt.Weapon.BleedDps; tgt.Rt.BleedExpiry = t + 4f; }

        if (tgt.Rt.Hp <= 0f)
        {
            atk.Kills++; tgt.S = FighterState.Down; tgt.Rt.StateTimer = 999f;
            tgt.Facing = atk.Facing == 1 ? -1 : 1;
        }
        else
        {
            // 좀비: HP≤30% 경직 면역. 그 외 강타/확률로 스태거
            bool immune = tgt.Rt.Has(TraitTable.Zombie) && tgt.Rt.Hp <= tgt.Rt.HpMax * 0.30f;
            if (!immune && tgt.S != FighterState.Guard && (atk.Rt.MotionKindNow == MotionKind.Heavy || crit || rng.Roll(0.25f)))
            { tgt.S = FighterState.Stagger; tgt.Rt.StateTimer = crit ? 0.5f : 0.35f; }
        }
    }

    private FighterRuntime MakeRuntime(int idx, FighterDef def, Vec2 pos)
    {
        var w = WeaponTable.Get(def.WeaponId);
        var rt = new FighterRuntime
        {
            Index = idx, Def = def, Weapon = w,
            Profile = TacticsTable.Get(def.TacticsId),
            Personality = PersonalityTable.Get(def.PersonalityId),
        };
        rt.HpMax = def.Stats.HpMax;
        rt.StaminaMax = 100f + def.Stats.Aspd * 0.3f;
        rt.MoveSpeed = (2.6f + def.Stats.Spd / 40f) * w.MoveSpeedMult;
        if (def.TraitIds != null)
            foreach (var id in def.TraitIds)
                if (TraitTable.Exists(id))
                {
                    rt.Traits.Add(id); var tr = TraitTable.Get(id);
                    rt.HpMax *= tr.HpMaxMult; rt.StaminaMax *= tr.StaminaMaxMult; rt.MoveSpeed *= tr.MoveSpeedMult;
                    rt.RangeMult *= tr.RangeMult; rt.RangeBonus += tr.RangeAdd; rt.DamageTakenMult *= tr.DamageTakenMult;
                    rt.SizeScale *= tr.SizeScale;
                }
        rt.Hp = rt.HpMax; rt.Stamina = rt.StaminaMax; rt.Pos = pos; rt.State = FighterState.Idle;
        return rt;
    }

    private static Frame Snap(List<M> us, float t) => new(t, us.Select(u => new FrameUnit(
        u.Rt.Pos.X, u.Rt.Pos.Y, MathF.Max(0f, u.Rt.Hp / u.Rt.HpMax), u.Rt.Stamina / u.Rt.StaminaMax,
        u.S.ToString(), u.Facing, u.Rt.MotionKindNow == MotionKind.Heavy && u.S is FighterState.Windup or FighterState.Active,
        u.Rt.BleedStacks, u.HitFlashFrame > 0f, u.Dead)).ToArray());

    private static int Judge(List<M> us)
    {
        float h0 = us.Where(u => u.Team == 0).Sum(u => MathF.Max(0f, u.Rt.Hp / u.Rt.HpMax));
        float h1 = us.Where(u => u.Team == 1).Sum(u => MathF.Max(0f, u.Rt.Hp / u.Rt.HpMax));
        return h0 >= h1 ? 0 : 1;
    }
}
