using Morituri.Sim.Core;
using Morituri.Sim.Data;

namespace Morituri.Sim.Match;

/// <summary>
/// 다대다 난투 시뮬레이터 (패싸움 전용). **1v1 MatchSim과 완전 분리** — 전투 매트릭스에 무영향.
/// 기존 sim의 자산(FighterRuntime·FighterState FSM·CombatMath·무기 데이터)을 재사용해 각 유닛이
/// 실제 공격 FSM(접근→윈드업→액티브→후딜, 피격 스태거·다운)을 거친다 → 뷰어가 1v1과 같은 자세로 렌더.
/// 정밀 역학(disc-strafe·포이즈게이지·가드·패링)은 없음(난투는 밸런스 무관). 결정론(단일 SimRandom).
/// </summary>
public sealed class MeleeSim
{
    private const float Dt = 1f / 60f;
    private const float MaxTime = 50f;
    private const float ArenaR = 9f;
    private const float FrameSampleSec = 1f / 20f;

    public sealed record Unit(string Name, int Team, string Weapon);
    public sealed record FrameUnit(float X, float Y, float HpPct, string State, int Facing, bool Heavy, bool Dead);
    public sealed record Frame(float T, FrameUnit[] Units);
    public sealed record Outcome(string Name, int Team, float DamageDealt, int Kills, bool Survived, float MinHpPct);
    public sealed record MeleeResult(int WinningTeam, string Reason, float DurationSec, List<Outcome> Outcomes);

    private sealed class M
    {
        public required FighterRuntime Rt; public required int Team;
        public float Dealt; public int Kills; public float MinHpPct = 1f;
        public int Facing = 1; public float NextReady;     // 다음 공격 가능 시각
        public int TargetIdx = -1;
        public bool Dead => Rt.Hp <= 0f && Rt.State == FighterState.Down;
        public FighterState S { get => Rt.State; set => Rt.State = value; }
    }

    public (MeleeResult Result, List<Frame> Frames, List<Unit> Units) Run(
        IReadOnlyList<(FighterDef Def, int Team)> roster, ulong seed)
    {
        var rng = new SimRandom(seed);
        var us = new List<M>();
        int[] tc = { roster.Count(r => r.Team == 0), roster.Count(r => r.Team == 1) };
        int[] placed = { 0, 0 };
        int idx = 0;
        foreach (var (def, team) in roster)
        {
            int n = tc[team]; int i = placed[team]++;
            float side = team == 0 ? -1f : 1f;
            float y = n <= 1 ? 0f : (i / (float)(n - 1) - 0.5f) * 6f;
            us.Add(new M { Rt = MakeRuntime(idx++, def, new Vec2(side * 4.8f, y)), Team = team, Facing = team == 0 ? 1 : -1 });
        }

        var meta = us.Select(u => new Unit(u.Rt.Def.Name, u.Team, u.Rt.Weapon.Id.Replace("WPN_", ""))).ToList();
        var frames = new List<Frame>();
        float t = 0f, nextFrame = 0f; string reason = "Judgement";

        for (; t < MaxTime; t += Dt)
        {
            foreach (var u in us)
            {
                if (u.Dead) continue;

                // 표적: 죽었거나 없으면 최근접 살아있는 적으로 갱신
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

                // 커밋 상태(윈드업/액티브/후딜/스태거/히트스턴/다운) 진행
                u.Rt.StateTimer -= Dt;
                if (u.S is FighterState.Windup or FighterState.Active or FighterState.Recovery
                        or FighterState.Stagger or FighterState.HitStun or FighterState.GetUp)
                {
                    if (u.Rt.StateTimer > 0f) continue;
                    switch (u.S)
                    {
                        case FighterState.Windup:
                            // 액티브 진입 = 타격 판정
                            u.S = FighterState.Active; u.Rt.StateTimer = 0.10f;
                            if (dist <= reach + 0.6f) Hit(u, tgt, rng);
                            break;
                        case FighterState.Active:
                            u.S = FighterState.Recovery; u.Rt.StateTimer = u.Rt.Weapon.RecoverySec;
                            break;
                        default:   // Recovery/Stagger/HitStun/GetUp 종료
                            u.S = FighterState.Idle; u.Rt.StateTimer = 0f;
                            break;
                    }
                    continue;
                }

                // 비커밋(Idle/Move): 접근 or 공격
                if (dist > reach)
                {
                    u.S = FighterState.Move;
                    float mv = u.Rt.MoveSpeed * Dt;
                    var dir = (tgt.Rt.Pos - u.Rt.Pos).Normalized();
                    var repel = new Vec2(0, 0);
                    foreach (var a in us)
                        if (a != u && !a.Dead && Vec2.Dist(a.Rt.Pos, u.Rt.Pos) < 1.1f)
                            repel += (u.Rt.Pos - a.Rt.Pos).Normalized() * 0.5f;
                    u.Rt.Pos += dir * mv + repel * Dt;
                    if (u.Rt.Pos.Length > ArenaR) u.Rt.Pos = u.Rt.Pos.Normalized() * ArenaR;
                }
                else if (t >= u.NextReady)
                {
                    u.S = FighterState.Windup;
                    u.Rt.MotionKindNow = (u.Rt.Weapon.HeavyBias > 0f && rng.Roll(0.6f)) ? MotionKind.Heavy : MotionKind.Light;
                    u.Rt.StateTimer = (0.28f + (u.Rt.MotionKindNow == MotionKind.Heavy ? 0.22f : 0f)) / u.Rt.Weapon.MotionSpeed;
                    float interval = 0.35f + 0.9f / (0.4f + u.Rt.Def.Stats.Aspd / 70f);
                    u.NextReady = t + interval;
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

    private static void Hit(M atk, M tgt, SimRandom rng)
    {
        if (tgt.Dead) return;
        float dmg = atk.Rt.Weapon.BaseDamage * atk.Rt.Weapon.HitCount
                  * (0.55f + atk.Rt.Def.Stats.Atk / 130f)
                  / (0.55f + tgt.Rt.Def.Stats.Def / 130f)
                  * (0.85f + rng.NextFloat01() * 0.3f);
        tgt.Rt.Hp -= dmg; atk.Dealt += dmg;
        tgt.MinHpPct = MathF.Min(tgt.MinHpPct, MathF.Max(0f, tgt.Rt.Hp / tgt.Rt.HpMax));
        if (tgt.Rt.Hp <= 0f)
        {
            atk.Kills++; tgt.S = FighterState.Down; tgt.Rt.StateTimer = 999f;
            tgt.Facing = atk.Facing == 1 ? -1 : 1;
        }
        else if (atk.Rt.MotionKindNow == MotionKind.Heavy || rng.Roll(0.28f))
        {
            // 강타/확률로 경직 — 상대 행동 중단
            tgt.S = FighterState.Stagger; tgt.Rt.StateTimer = 0.35f;
        }
    }

    private FighterRuntime MakeRuntime(int idx, FighterDef def, Vec2 pos)
    {
        // 기존 runtime 재사용 — 파생 스탯은 CombatMath로(1v1과 같은 공식). FSM은 MeleeSim이 구동.
        var w = WeaponTable.Get(def.WeaponId);
        var rt = new FighterRuntime
        {
            Index = idx, Def = def, Weapon = w,
            Profile = TacticsTable.Get(def.TacticsId),
            Personality = PersonalityTable.Get(def.PersonalityId),
        };
        rt.HpMax = def.Stats.HpMax;
        rt.MoveSpeed = 2.6f + def.Stats.Spd / 40f;   // 난투 이동(무기 배율은 EffRange로 충분히 반영)
        rt.MoveSpeed *= w.MoveSpeedMult;
        if (def.TraitIds != null)
            foreach (var id in def.TraitIds)
                if (TraitTable.Exists(id)) { var tr = TraitTable.Get(id); rt.HpMax *= tr.HpMaxMult; rt.MoveSpeed *= tr.MoveSpeedMult; rt.RangeMult *= tr.RangeMult; rt.RangeBonus += tr.RangeAdd; }
        rt.Hp = rt.HpMax; rt.Pos = pos; rt.State = FighterState.Idle;
        return rt;
    }

    private static Frame Snap(List<M> us, float t) => new(t, us.Select(u => new FrameUnit(
        u.Rt.Pos.X, u.Rt.Pos.Y, MathF.Max(0f, u.Rt.Hp / u.Rt.HpMax),
        u.S.ToString(), u.Facing, u.Rt.MotionKindNow == MotionKind.Heavy && u.S is FighterState.Windup or FighterState.Active,
        u.Dead)).ToArray());

    private static int Judge(List<M> us)
    {
        float h0 = us.Where(u => u.Team == 0).Sum(u => MathF.Max(0f, u.Rt.Hp / u.Rt.HpMax));
        float h1 = us.Where(u => u.Team == 1).Sum(u => MathF.Max(0f, u.Rt.Hp / u.Rt.HpMax));
        return h0 >= h1 ? 0 : 1;
    }
}
