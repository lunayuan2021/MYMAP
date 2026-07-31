using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps replay-only runtime systems attached on any replay scene load, so
/// Editor Play and WebGL use the same component set.
/// </summary>
static class LycheeReplaySceneBootstrapper
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        AttachToReplayPlayers();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        AttachToReplayPlayers();
    }

    static void AttachToReplayPlayers()
    {
        LycheeReplayPlayer[] players = Object.FindObjectsByType<LycheeReplayPlayer>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        foreach (LycheeReplayPlayer player in players)
        {
            if (player == null) continue;

            Ensure<LycheeCinematicDirector>(player, LycheeCinematicDirector.Disabled);
            Ensure<LycheeOpeningShowcase>(player, LycheeOpeningShowcase.Disabled);
            Ensure<LycheeMinimapSystem>(player, LycheeMinimapSystem.Disabled);
            Ensure<LycheeHudExtensions>(player, LycheeHudExtensions.Disabled);
            Ensure<LycheeVideoInterlude>(player);
            Ensure<LycheeCombatPresentation>(player, LycheeCombatPresentation.Disabled);
            Ensure<LycheeCardFxSystem>(player, LycheeCardFxSystem.Disabled);
            Ensure<LycheeConvoyVfx>(player, LycheeConvoyVfx.Disabled);
            Ensure<LycheeCharacterMotion>(player, LycheeCharacterMotion.Disabled);
            Ensure<LycheeWorldMarkerSystem>(player, LycheeWorldMarkerSystem.Disabled);
            Ensure<LycheeWeatherFxSystem>(player, LycheeWeatherFxSystem.Disabled);
            Ensure<LycheeAudioDirector>(player, LycheeAudioDirector.Disabled);
            Ensure<LycheeLabelUpgrade>(player, LycheeLabelUpgrade.Disabled);
            Ensure<LycheeContestBuildingXray>(player);
            Ensure<LycheeSettlementScreen>(player, LycheeSettlementScreen.Disabled);
        }
    }

    static void Ensure<T>(LycheeReplayPlayer player, bool disabled = false) where T : Component
    {
        if (disabled || player.GetComponent<T>() != null) return;
        player.gameObject.AddComponent<T>();
    }
}
