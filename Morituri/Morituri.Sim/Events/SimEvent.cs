namespace Morituri.Sim.Events;

using Morituri.Sim.Match;

/// <summary>
/// 문서[1] 4.1 SimEvent 최소셋. Sim은 매 Tick "무슨 일이 있었는지"를 이벤트로만 말한다.
/// Unity Bridge와 Headless 통계 집계가 같은 스트림을 소비한다.
/// </summary>
public abstract record SimEvent(float Time);

public sealed record StateChanged(float Time, int FighterId, FighterState From, FighterState To) : SimEvent(Time);
public sealed record AttackSwung(float Time, int FighterId, string MotionId, bool IsFeint) : SimEvent(Time);
public sealed record HitLanded(float Time, int Attacker, int Defender, float Damage,
                               bool IsCrit, bool IsCounter, bool IsGuarded) : SimEvent(Time);
public sealed record PoiseBroken(float Time, int FighterId) : SimEvent(Time);
public sealed record GuardBroken(float Time, int FighterId) : SimEvent(Time);
public sealed record KnockedDown(float Time, int FighterId) : SimEvent(Time);
public sealed record Decision(float Time, int FighterId, string ReasonTag, string SourceLayer, float Duration) : SimEvent(Time);
public sealed record StaminaExhausted(float Time, int FighterId) : SimEvent(Time);
public sealed record MatchEnded(float Time, int Winner, string Reason, float ScoreA, float ScoreB) : SimEvent(Time);
