using Morituri.Sim.Core;
using Morituri.Sim.Data;

namespace Morituri.Sim.Match;

/// <summary>
/// 다대다 난투 시뮬레이터 (패싸움 전용). **1v1 MatchSim과 완전 분리** — 전투 매트릭스에 무영향.
/// 정밀 역학(disc-strafe·포이즈·가드·패링) 없음. N명이 팀을 이뤄 동시에 움직이며 가장 가까운 적을 치는
/// 경량 난전 모델. 스탯(ATK/DEF/HP/SPD/ASPD)·무기 사거리/데미지만 사용. 결정론(단일 SimRandom).
/// </summary>
public sealed class MeleeSim
{
    private const float Dt = 1f / 30f;          // 30Hz(난투는 정밀도보다 규모)
    private const float MaxTime = 45f;
    private const float ArenaR = 8.5f;
    private const float FrameSampleSec = 1f / 15f;

    public sealed record Unit(string Name, int Team, string Weapon);
    public sealed record FrameUnit(float X, float Y, float HpPct, bool Attacking, bool Dead);
    public sealed record Frame(float T, FrameUnit[] Units);
    public sealed record Outcome(string Name, int Team, float DamageDealt, int Kills, bool Survived, float MinHpPct);
    public sealed record MeleeResult(int WinningTeam, string Reason, float DurationSec, List<Outcome> Outcomes);

    private sealed class M
    {
        public required FighterDef Def; public required int Team; public required WeaponDef W;
        public Vec2 Pos; public float Hp, HpMax, MinHpPct = 1f, Dealt; public int Kills;
        public float NextAtk; public float AtkFlashUntil; public bool Dead => Hp <= 0f;
    }

    /// <summary>난투 실행. teams: (def, team 0/1). 반환: 결과 + 프레임(뷰어) + 유닛 메타.</summary>
    public (MeleeResult Result, List<Frame> Frames, List<Unit> Units) Run(
        IReadOnlyList<(FighterDef Def, int Team)> roster, ulong seed)
    {
        var rng = new SimRandom(seed);
        var units = new List<M>();
        // 초기 배치: 팀0 왼쪽 호, 팀1 오른쪽 호 — 세로로 벌려 세움
        int[] teamCount = { roster.Count(r => r.Team == 0), roster.Count(r => r.Team == 1) };
        int[] placed = { 0, 0 };
        foreach (var (def, team) in roster)
        {
            var w = WeaponTable.Get(def.WeaponId);
            int n = teamCount[team]; int i = placed[team]++;
            float side = team == 0 ? -1f : 1f;
            float y = n <= 1 ? 0f : (i / (float)(n - 1) - 0.5f) * 6f;   // −3..+3 세로 분산
            units.Add(new M { Def = def, Team = team, W = w,
                Pos = new Vec2(side * 4.5f, y), Hp = def.Stats.HpMax, HpMax = def.Stats.HpMax });
        }

        var frames = new List<Frame>();
        var meta = units.Select(u => new Unit(u.Def.Name, u.Team, u.W.Id.Replace("WPN_", ""))).ToList();
        float t = 0f, nextFrame = 0f;
        string reason = "Judgement";

        for (; t < MaxTime; t += Dt)
        {
            foreach (var u in units)
            {
                if (u.Dead) continue;
                u.AtkFlashUntil = u.AtkFlashUntil > t ? u.AtkFlashUntil : 0f;
                // 표적: 가장 가까운 살아있는 적
                M? tgt = null; float best = float.MaxValue;
                foreach (var e in units)
                {
                    if (e.Dead || e.Team == u.Team) continue;
                    float d = Vec2.Dist(u.Pos, e.Pos);
                    if (d < best) { best = d; tgt = e; }
                }
                if (tgt == null) continue;

                float reach = u.W.Range + 0.4f;
                if (best > reach)
                {
                    // 접근 — SPD 기반 이동속도(m/s), 무기 중량 배율
                    float mv = (2.6f + u.Def.Stats.Spd / 45f) * u.W.MoveSpeedMult * Dt;
                    var dir = (tgt.Pos - u.Pos).Normalized();
                    // 살짝 옆으로 흩어져 뭉침 방지(팀메이트 간 반발)
                    var repel = new Vec2(0, 0);
                    foreach (var a in units)
                        if (a != u && !a.Dead && Vec2.Dist(a.Pos, u.Pos) < 1.1f)
                            repel += (u.Pos - a.Pos).Normalized() * 0.4f;
                    u.Pos += dir * mv + repel * Dt;
                    float r = u.Pos.Length;
                    if (r > ArenaR) u.Pos = u.Pos.Normalized() * ArenaR;   // 아레나 밖 금지
                }
                else if (t >= u.NextAtk)
                {
                    // 공격 — ATK vs DEF, 무기 데미지·변동. 경직/가드 없음(난전 = 난타).
                    float dmg = u.W.BaseDamage * u.W.HitCount
                              * (0.55f + u.Def.Stats.Atk / 130f)
                              / (0.55f + tgt.Def.Stats.Def / 130f)
                              * (0.85f + rng.NextFloat01() * 0.3f);
                    tgt.Hp -= dmg; u.Dealt += dmg;
                    tgt.MinHpPct = MathF.Min(tgt.MinHpPct, MathF.Max(0f, tgt.Hp / tgt.HpMax));
                    u.AtkFlashUntil = t + 0.18f;
                    // 공격 간격: ASPD·무기 모션 (느린 무기일수록 김)
                    u.NextAtk = t + (0.75f + 0.9f / (0.4f + u.Def.Stats.Aspd / 70f)) / u.W.MotionSpeed;
                    if (tgt.Hp <= 0f) u.Kills++;
                }
            }

            if (t >= nextFrame)
            {
                frames.Add(new Frame(t, units.Select(u => new FrameUnit(
                    u.Pos.X, u.Pos.Y, MathF.Max(0f, u.Hp / u.HpMax), u.AtkFlashUntil > t, u.Dead)).ToArray()));
                nextFrame += FrameSampleSec;
            }

            int alive0 = units.Count(u => u.Team == 0 && !u.Dead);
            int alive1 = units.Count(u => u.Team == 1 && !u.Dead);
            if (alive0 == 0 || alive1 == 0) { reason = "KO"; t += Dt; break; }
        }

        // 마지막 프레임
        frames.Add(new Frame(t, units.Select(u => new FrameUnit(
            u.Pos.X, u.Pos.Y, MathF.Max(0f, u.Hp / u.HpMax), false, u.Dead)).ToArray()));

        int a0 = units.Count(u => u.Team == 0 && !u.Dead), a1 = units.Count(u => u.Team == 1 && !u.Dead);
        int winner = a0 == a1 ? Judge(units) : (a0 > a1 ? 0 : 1);
        var outcomes = units.Select(u => new Outcome(u.Def.Name, u.Team,
            MathF.Round(u.Dealt), u.Kills, !u.Dead, u.MinHpPct)).ToList();
        return (new MeleeResult(winner, reason, t, outcomes), frames, meta);
    }

    // 생존 동수 시: 팀 총 HP% 비교
    private static int Judge(List<M> units)
    {
        float h0 = units.Where(u => u.Team == 0).Sum(u => MathF.Max(0f, u.Hp / u.HpMax));
        float h1 = units.Where(u => u.Team == 1).Sum(u => MathF.Max(0f, u.Hp / u.HpMax));
        return h0 >= h1 ? 0 : 1;
    }
}
