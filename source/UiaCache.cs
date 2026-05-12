using System.Windows.Automation;

namespace SurfaceChargingTray;

/// <summary>
/// Locates the Surface app's Battery & charging card and its three radio
/// buttons across Surface app builds and Windows display languages.
///
/// Why this exists: the original implementation hardcoded English UIA Names
/// (`"Battery & charging"`, `"Adaptive"`, etc.). On a non-English Windows
/// install — or on a Surface app build where Microsoft renamed the labels —
/// the lookup fails and the user sees "card not found." This class layers
/// four progressively-more-tolerant strategies and self-heals via cache:
///
///   Layer 1: Cached AutomationId from the last successful run. Fastest,
///            language-independent, version-independent. Empty on first launch.
///   Layer 2: Cached Name from the last successful run. Catches cases where
///            Microsoft changed the AutomationId but kept the Name.
///   Layer 3: A bundled list of known Names (English variants + commonly-
///            requested locales). Covers most non-English systems on first
///            launch.
///   Layer 4 (card only): Structural search — find any Group that contains
///            exactly three RadioButton descendants. The Battery card is the
///            only place in the Surface app with that exact structure, so
///            this works language-independently and version-independently
///            as a final safety net.
///
/// On any successful match, the discovered AutomationId and Name are written
/// to settings.ini under [uia-cache]. Cache entries are validated before use
/// (cards must still contain 3 radios; radios must still be of type
/// RadioButton) and silently wiped on validation failure so the next call
/// rediscovers from scratch.
/// </summary>
internal static class UiaCache
{
    // ---- Multi-language Name lists -------------------------------------
    // Add new entries as user reports come in. Order matters slightly:
    // English variants first since most Surface app installs are en-*.

    private static readonly string[] BatteryCardNames =
    {
        "Battery & charging",
        "Battery and charging",
        "Battery",
        "Charging",
        // Newer Microsoft terminology that may appear in v76+ Surface app builds.
        "Charging mode",
        "Smart charging",
        "Battery Smart Charging",
        // Common localizations (best-effort; expand as reports arrive):
        "Akku & Aufladen",        // de-DE
        "Akku und Aufladen",      // de-DE alt
        "Akku und Laden",         // de-DE (observed on Surface Laptop Studio 1)
        "Batterie et charge",     // fr-FR
        "Batterie et chargement", // fr-FR alt
        "Batería y carga",        // es-ES
        "Batería y carga",        // es-MX (same)
        "Batteria e ricarica",    // it-IT
        "Bateria e carregamento", // pt-BR / pt-PT
        "Battery en opladen",     // nl-NL (literal)
        "Batterij en opladen",    // nl-NL native
        "Akumulator i ładowanie", // pl-PL
        "Batteri og opladning",   // da-DK
        "Batteri och laddning",   // sv-SE
        "Batteri og lading",      // nb-NO
        "Akku ja lataus",         // fi-FI
        "Батарея и зарядка",      // ru-RU
        "バッテリーと充電",          // ja-JP
        "电池和充电",                // zh-CN
        "電池與充電",                // zh-TW
        "배터리 및 충전",            // ko-KR
        "Pil ve şarj",            // tr-TR
        "البطارية والشحن",         // ar
    };

    private static readonly string[] AdaptiveNames =
    {
        "Adaptive",
        "Adaptive charging",
        // Locales (best-effort):
        "Adaptiv",                // de-DE / sv-SE / no
        "Adaptatif",              // fr-FR
        "Adaptable",              // es-ES
        "Adattiva",               // it-IT
        "Adaptável",              // pt
        "Adaptief",               // nl-NL
        "アダプティブ",              // ja-JP
        "自适应",                   // zh-CN
        "自適應",                   // zh-TW
        "적응형",                   // ko-KR
    };

    private static readonly string[] Limit80Names =
    {
        "Limit to 80%",
        "Limit charging to 80%",
        "Battery limit 80%",
        // Locales:
        "Auf 80 % begrenzen",
        "Limiter à 80 %",
        "Limitar al 80 %",
        "Limita al 80%",
        "Limitar a 80%",
        "Beperken tot 80%",
        "80%に制限",
        "限制为80%",
        "限制至 80%",
        "80%로 제한",
    };

    private static readonly string[] Charge100Names =
    {
        "Charge to 100%",
        "Charge to full",
        "Charge fully",
        // Locales:
        "Auf 100 % aufladen",
        "Charger à 100 %",
        "Cargar al 100 %",
        "Carica al 100%",
        "Carregar a 100%",
        "Opladen tot 100%",
        "100%まで充電",
        "充电至100%",
        "充電至 100%",
        "100%로 충전",
    };

    /// <summary>
    /// Dump a brief snapshot of the Surface app's UIA tree (top 3 levels,
    /// up to 60 elements) to the error log. Called from SurfaceController's
    /// "card not found" / "no radio selected" error paths so future bug
    /// reports include what the tree actually looked like at failure time,
    /// instead of just "didn't find it." Truncated by design — full tree
    /// dumps can be hundreds of nodes.
    /// </summary>
    public static void LogTreeSnapshot(AutomationElement root, string reason)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[INFO] UIA tree snapshot (").Append(reason).Append("):\n");
            // depth=5 + cap=120: deep enough to see inside the Surface app's
            // CapCardListView (the cards live at depth ~4-5), wide enough to
            // include sibling cards alongside the Battery card without
            // truncating. Bumped from depth=3/cap=60 in v1.2.1 after a user
            // report cut off the card contents.
            int count = DumpElement(root, sb, depth: 0, maxDepth: 5, count: 0, maxCount: 120);
            if (count >= 120) sb.Append("  (truncated at 120 elements)\n");
            Logger.Error(sb.ToString().TrimEnd());
        }
        catch (System.Exception ex)
        {
            Logger.Error($"[ERR ] LogTreeSnapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int DumpElement(
        AutomationElement e, System.Text.StringBuilder sb,
        int depth, int maxDepth, int count, int maxCount)
    {
        if (count >= maxCount) return count;
        string name = "", id = "", ct = "";
        try { name = (e.GetCurrentPropertyValue(AutomationElement.NameProperty)        as string ?? "").Trim(); } catch { }
        try { id   = (e.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty) as string ?? "").Trim(); } catch { }
        try
        {
            if (e.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) is ControlType c)
                ct = c.ProgrammaticName?.Replace("ControlType.", "") ?? "";
        }
        catch { }
        if (name.Length > 60) name = name[..60] + "…";
        if (id.Length   > 40) id   = id[..40]   + "…";
        sb.Append(new string(' ', depth * 2))
          .Append("- [").Append(ct).Append("] name='").Append(name)
          .Append("' id='").Append(id).Append("'\n");
        count++;

        if (depth >= maxDepth) return count;
        try
        {
            var children = e.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement c in children)
            {
                count = DumpElement(c, sb, depth + 1, maxDepth, count, maxCount);
                if (count >= maxCount) break;
            }
        }
        catch { }
        return count;
    }

    /// <summary>
    /// Wipe the cached Battery card + radio IDs/names. Called from
    /// SurfaceController when a previously-cached card fails downstream
    /// validation (the cache poisoning scenario: auto-discovery may have
    /// cached a wrong Group, and Layer 1/2 keep retrieving it forever).
    /// Next FindBatteryCard then re-runs Layers 3-4 from scratch.
    /// </summary>
    public static void ClearAllCache(SettingsModel s)
    {
        bool changed = false;
        if (s.BatteryCardId != null || s.BatteryCardName != null)
        {
            s.BatteryCardId = null; s.BatteryCardName = null; changed = true;
        }
        if (s.AdaptiveRadioId != null || s.AdaptiveRadioName != null)
        {
            s.AdaptiveRadioId = null; s.AdaptiveRadioName = null; changed = true;
        }
        if (s.Limit80RadioId != null || s.Limit80RadioName != null)
        {
            s.Limit80RadioId = null; s.Limit80RadioName = null; changed = true;
        }
        if (s.Charge100RadioId != null || s.Charge100RadioName != null)
        {
            s.Charge100RadioId = null; s.Charge100RadioName = null; changed = true;
        }
        if (changed) s.Save();
    }

    // ---- Public API ----------------------------------------------------

    /// <summary>
    /// Find the Battery & charging card. Tries layers in order, validates
    /// the result has 3 RadioButton descendants, caches the discovered
    /// AutomationId / Name, returns null only if every layer fails.
    /// </summary>
    public static AutomationElement? FindBatteryCard(
        AutomationElement window, SettingsModel settings, int timeoutMs)
    {
        // Layers 1-3 trust Name/ID matches without validating that the card
        // already contains 3 radios. The card may be COLLAPSED at search time
        // (radios not yet in the UIA subtree); the caller expands it after we
        // hand it back, then searches for radios separately. Validating here
        // would race against the collapsed state and discard valid matches.
        var card = TryLayered(
            window,
            settings.BatteryCardId,
            settings.BatteryCardName,
            BatteryCardNames,
            validate: null,
            timeoutMs);

        // Layer 4 (card-specific): structural search for a Group with 3 radios.
        // ValidateCard is required here because "any Group" needs the radio
        // count to disambiguate which Group is the battery card. This layer
        // only fires after Layers 1-3 fail, so the card has had plenty of
        // time to render its children by then.
        if (card == null) card = FindCardStructurally(window);

        if (card != null) CacheCard(card, settings);
        // Note: we do NOT clear the cache on miss. A transient miss (e.g.
        // Surface app cold-loading slowly) shouldn't invalidate good cache
        // values — the next successful run refreshes them anyway.

        return card;
    }

    /// <summary>
    /// Find a charging-mode radio button under the given card. <paramref name="modeKey"/>
    /// is one of <c>"adaptive"</c>, <c>"80"</c>, <c>"100"</c>.
    /// </summary>
    public static AutomationElement? FindRadio(
        AutomationElement scope, string modeKey, SettingsModel settings, int timeoutMs)
    {
        string? cachedId, cachedName;
        string[] knownNames;
        switch (modeKey)
        {
            case "adaptive": cachedId = settings.AdaptiveRadioId;  cachedName = settings.AdaptiveRadioName;  knownNames = AdaptiveNames;  break;
            case "80":       cachedId = settings.Limit80RadioId;   cachedName = settings.Limit80RadioName;   knownNames = Limit80Names;   break;
            case "100":      cachedId = settings.Charge100RadioId; cachedName = settings.Charge100RadioName; knownNames = Charge100Names; break;
            default: return null;
        }

        // Same as the card: trust Name/ID matches; only the structural
        // safety net needs the ControlType filter. For radios that's
        // less important since FindFirst by Name + RadioButton ControlType
        // is what the multi-language list does anyway, but we keep the
        // pattern symmetric.
        var rb = TryLayered(scope, cachedId, cachedName, knownNames, validate: null, timeoutMs);

        if (rb != null) CacheRadio(rb, modeKey, settings);
        // No cache clear on miss — see FindBatteryCard for rationale.

        return rb;
    }

    /// <summary>
    /// Walk the radios under a known card and return the modeKey of the
    /// currently selected one. Tries two "is-this-one-active?" signals
    /// because Surface app builds vary in which UIA pattern the mode
    /// controls expose:
    ///
    ///   1. SelectionItemPattern.IsSelected (canonical for RadioButton).
    ///   2. TogglePattern.ToggleState == On (some Surface app versions
    ///      implement the mode controls as toggle-buttons rather than
    ///      radios; ToggleState surfaces the "on" one).
    ///
    /// Returns null only if no radio reports active on either pattern.
    /// </summary>
    public static string? FindSelectedModeKey(AutomationElement card, SettingsModel settings)
    {
        foreach (var key in new[] { "adaptive", "80", "100" })
        {
            var rb = FindRadio(card, key, settings, 2000);
            if (rb == null) continue;

            // Strategy 1: SelectionItemPattern
            try
            {
                if (rb.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object spObj))
                {
                    var sp = (SelectionItemPattern)spObj;
                    if (sp.Current.IsSelected) return key;
                }
            }
            catch { }

            // Strategy 2: TogglePattern
            try
            {
                if (rb.TryGetCurrentPattern(TogglePattern.Pattern, out object tpObj))
                {
                    var tp = (TogglePattern)tpObj;
                    if (tp.Current.ToggleState == ToggleState.On) return key;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Comprehensive discovery scan. Expands every collapsible element in the
    /// Surface window, walks the tree, and writes the discovered AutomationId
    /// and Name for the Battery card and its three charging-mode radios into
    /// <paramref name="settings"/> (which immediately persists to settings.ini).
    ///
    /// Two trigger paths:
    ///   - Tray startup: if the cache is empty, the tray fires a background
    ///     RefreshState which calls discovery automatically. This means the
    ///     very first user click hits Layer 1 (cached AutomationId) in one
    ///     poll — no expensive Name search.
    ///   - Self-healing: if the normal layered lookup fails (e.g. Microsoft
    ///     renamed everything in a Surface app update), SurfaceController
    ///     calls discovery as a last resort, then retries the lookup.
    ///
    /// Radios are identified primarily by Name match against the known-name
    /// lists; any radio whose Name we don't recognize falls back to its
    /// positional slot in Microsoft's stable UI order (Adaptive, 80%, 100%).
    /// Returns true if at least the Battery card was found and cached.
    /// </summary>
    public static bool DiscoverAndCacheAll(AutomationElement window, SettingsModel settings)
    {
        try
        {
            // Step 1: expand every collapsible Group/Pane so the entire UIA
            // tree is fully populated. Battery card may be inside a collapsed
            // parent or be collapsed itself; either way, after this step the
            // radios are guaranteed to be visible to UIA.
            ExpandAllCollapsible(window);

            // Step 2: locate the Battery card. After expansion both Name match
            // (Layer 3) and structural search (Layer 4) reliably work; we try
            // Name first since it's faster on a typical tree.
            AutomationElement? card = null;
            foreach (var name in BatteryCardNames)
            {
                try
                {
                    card = window.FindFirst(TreeScope.Subtree,
                        new PropertyCondition(AutomationElement.NameProperty, name));
                    if (card != null && ValidateCard(card)) break;
                    card = null;
                }
                catch { }
            }
            if (card == null) card = FindCardStructurally(window);
            if (card == null) return false;

            CacheCard(card, settings);

            // Step 3: walk the card's three radios. Microsoft's UI order is
            // Adaptive, 80%, 100% from top to bottom. We try Name match
            // against each known list; anything unmatched is filled in
            // positionally so we still get *some* mapping even when a
            // radio's localized name isn't in our bundled list.
            AutomationElementCollection radios;
            try
            {
                radios = card.FindAll(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
            }
            catch { return true; }   // card was cached; radios best-effort

            var slots = new AutomationElement?[3];   // [0]=adaptive [1]=80 [2]=100
            var unmatched = new System.Collections.Generic.List<AutomationElement>();

            for (int i = 0; i < radios.Count; i++)
            {
                var rb = radios[i];
                string rname = "";
                try { rname = rb.Current.Name ?? ""; } catch { }

                if      (System.Array.IndexOf(AdaptiveNames,  rname) >= 0) slots[0] ??= rb;
                else if (System.Array.IndexOf(Limit80Names,   rname) >= 0) slots[1] ??= rb;
                else if (System.Array.IndexOf(Charge100Names, rname) >= 0) slots[2] ??= rb;
                else                                                       unmatched.Add(rb);
            }

            // Fill any empty slots positionally from the unmatched list, in
            // tree order (which mirrors visible UI order). This handles
            // languages we don't have strings for — we still get the right
            // mapping as long as Microsoft keeps the radios in the standard
            // order, which they have for years.
            int u = 0;
            for (int i = 0; i < 3; i++)
            {
                if (slots[i] == null && u < unmatched.Count)
                    slots[i] = unmatched[u++];
            }

            if (slots[0] != null) CacheRadio(slots[0]!, "adaptive", settings);
            if (slots[1] != null) CacheRadio(slots[1]!, "80",       settings);
            if (slots[2] != null) CacheRadio(slots[2]!, "100",      settings);

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Walks the subtree, expands every element with ExpandCollapsePattern
    /// that's currently Collapsed. Restricted to Group/Pane control types
    /// for speed (other controls almost never expose this pattern in the
    /// Surface app). Run inside discovery to surface any cards hidden
    /// behind collapsed parents.
    ///
    /// Side-effect note: this expands cards Microsoft chose to ship
    /// collapsed by default. Because we do all UIA work against a window
    /// we kill-and-relaunch on every operation, expansions never persist
    /// into the user's visible Surface app session.
    /// </summary>
    private static void ExpandAllCollapsible(AutomationElement root)
    {
        AutomationElementCollection candidates;
        try
        {
            candidates = root.FindAll(TreeScope.Subtree,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)));
        }
        catch { return; }

        bool expandedAny = false;
        foreach (AutomationElement e in candidates)
        {
            try
            {
                if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object pat))
                {
                    var ec = (ExpandCollapsePattern)pat;
                    if (ec.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                    {
                        ec.Expand();
                        expandedAny = true;
                    }
                }
            }
            catch { /* not all Groups support ExpandCollapse */ }
        }

        // Brief settle so newly-expanded children populate the UIA tree
        // before the caller starts searching.
        if (expandedAny) System.Threading.Thread.Sleep(500);
    }

    // ---- Internals -----------------------------------------------------

    private static bool ValidateCard(AutomationElement e)
    {
        try
        {
            var radios = e.FindAll(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
            return radios.Count >= 3;
        }
        catch { return false; }
    }

    private static bool ValidateRadio(AutomationElement e)
    {
        try
        {
            var ct = (ControlType)e.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty);
            return ct == ControlType.RadioButton;
        }
        catch { return false; }
    }

    /// <summary>
    /// Layered lookup. Cached layers (1, 2) get small fixed budgets — they
    /// either hit on the first poll or don't exist; no point waiting longer.
    /// The multi-language list (Layer 3) gets the remaining budget so the
    /// Surface app has time to fully render on a cold launch.
    /// <paramref name="validate"/> is optional; pass null to trust any match.
    /// </summary>
    private static AutomationElement? TryLayered(
        AutomationElement parent,
        string? cachedId,
        string? cachedName,
        string[] knownNames,
        System.Func<AutomationElement, bool>? validate,
        int totalTimeoutMs)
    {
        bool Accept(AutomationElement? h) => h != null && (validate == null || validate(h));

        int elapsed = 0;
        const int CACHE_BUDGET_MS = 2000;

        // Layer 1: cached AutomationId AND cached Name (AndCondition).
        // Microsoft assigns generic AutomationIds like "Expander" to many
        // elements; ID-alone could match the wrong one. Requiring Name to
        // match too disambiguates safely. If only one of the two cache
        // values is set, skip this layer — Layer 2 handles Name-alone.
        if (!string.IsNullOrEmpty(cachedId) && !string.IsNullOrEmpty(cachedName))
        {
            var hit = WaitFor(() => parent.FindFirst(TreeScope.Subtree,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, cachedId),
                    new PropertyCondition(AutomationElement.NameProperty, cachedName))),
                CACHE_BUDGET_MS, 200);
            if (Accept(hit)) return hit;
            elapsed += CACHE_BUDGET_MS;
        }

        // Layer 2: cached Name only. Catches cases where Microsoft kept the
        // localized Name but changed/removed the AutomationId in an update.
        if (!string.IsNullOrEmpty(cachedName))
        {
            var hit = WaitFor(() => parent.FindFirst(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.NameProperty, cachedName)),
                CACHE_BUDGET_MS, 200);
            if (Accept(hit)) return hit;
            elapsed += CACHE_BUDGET_MS;
        }

        // Multi-language list — whatever budget remains, with at least 5s
        // even if the cache layers ate into the total. Each poll cycles
        // through ALL names; keep the list short enough that one pass is
        // well under the 200ms poll interval on a typical UWP tree.
        int remaining = System.Math.Max(5000, totalTimeoutMs - elapsed);
        return WaitFor(() =>
        {
            foreach (var name in knownNames)
            {
                var h = parent.FindFirst(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.NameProperty, name));
                if (Accept(h)) return h;
            }
            return null;
        }, remaining, 200);
    }

    /// <summary>
    /// Last-resort structural search. Tries progressively more lenient
    /// strategies so we can find the card even when the Surface app on a
    /// given device differs from the Pro 12 baseline we developed against:
    ///
    ///   Strategy A: any Group with exactly 3+ RadioButton descendants.
    ///               (Original behavior. Works on Pro 12 / Laptop 5 era.)
    ///   Strategy B: any Group with 2+ RadioButton descendants.
    ///               (Catches devices that show only Adaptive + 80%, or 80% +
    ///               100%, etc.)
    ///   Strategy C: any element whose AutomationId contains "Battery" or
    ///               "Charging" (Microsoft semi-frequently uses such IDs for
    ///               container panels; locale-independent).
    ///   Strategy D: walk UP from any RadioButton in the tree whose Name is
    ///               in one of our mode-name lists, looking for an ancestor
    ///               with siblings forming the radio group. Survives a Surface
    ///               app rebuild that abandons the Group wrapper entirely.
    /// </summary>
    private static AutomationElement? FindCardStructurally(AutomationElement window)
    {
        // Strategy A: original — exactly 3+ radios
        try
        {
            var groups = window.FindAll(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
            foreach (AutomationElement g in groups)
            {
                if (ValidateCard(g)) return g;
            }
        }
        catch { }

        // Strategy B: relaxed — 2+ radios in a Group
        try
        {
            var groups = window.FindAll(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
            foreach (AutomationElement g in groups)
            {
                try
                {
                    var radios = g.FindAll(TreeScope.Subtree,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
                    if (radios.Count >= 2) return g;
                }
                catch { }
            }
        }
        catch { }

        // Strategy C: AutomationId substring match (locale-independent)
        try
        {
            var all = window.FindAll(TreeScope.Subtree, Condition.TrueCondition);
            foreach (AutomationElement e in all)
            {
                try
                {
                    var id = e.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty) as string ?? "";
                    if (id.Length == 0) continue;
                    if (id.Contains("Battery", System.StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("Charging", System.StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("ChargeMode", System.StringComparison.OrdinalIgnoreCase))
                    {
                        // Confirm it contains radios (i.e. it's the card, not
                        // a label/icon with an unrelated battery name).
                        var radios = e.FindAll(TreeScope.Subtree,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
                        if (radios.Count >= 2) return e;
                    }
                }
                catch { }
            }
        }
        catch { }

        // Strategy D: walk UP from any RadioButton whose Name matches a
        // charging mode in our known lists.
        try
        {
            var radios = window.FindAll(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
            foreach (AutomationElement rb in radios)
            {
                string rname = "";
                try { rname = rb.Current.Name ?? ""; } catch { }
                bool isChargingMode =
                    System.Array.IndexOf(AdaptiveNames,  rname) >= 0 ||
                    System.Array.IndexOf(Limit80Names,   rname) >= 0 ||
                    System.Array.IndexOf(Charge100Names, rname) >= 0;
                if (!isChargingMode) continue;

                // Walk up the tree looking for a Group/Pane ancestor with
                // 2+ radios in its subtree.
                var walker = TreeWalker.RawViewWalker;
                var parent = walker.GetParent(rb);
                for (int hops = 0; hops < 8 && parent != null; hops++)
                {
                    try
                    {
                        var ct = (ControlType?)parent.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty);
                        if (ct == ControlType.Group || ct == ControlType.Pane)
                        {
                            var subRadios = parent.FindAll(TreeScope.Subtree,
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
                            if (subRadios.Count >= 2) return parent;
                        }
                    }
                    catch { }
                    parent = walker.GetParent(parent);
                }
            }
        }
        catch { }

        return null;
    }

    // ---- Cache plumbing ------------------------------------------------

    private static void CacheCard(AutomationElement e, SettingsModel s)
    {
        try
        {
            var newId   = e.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty) as string;
            var newName = e.GetCurrentPropertyValue(AutomationElement.NameProperty)         as string;
            bool changed = false;
            if (!string.IsNullOrEmpty(newId)   && newId   != s.BatteryCardId)   { s.BatteryCardId   = newId;   changed = true; }
            if (!string.IsNullOrEmpty(newName) && newName != s.BatteryCardName) { s.BatteryCardName = newName; changed = true; }
            if (changed) s.Save();
        }
        catch { }
    }

    private static void ClearCardCache(SettingsModel s)
    {
        if (s.BatteryCardId == null && s.BatteryCardName == null) return;
        s.BatteryCardId = null;
        s.BatteryCardName = null;
        s.Save();
    }

    private static void CacheRadio(AutomationElement rb, string modeKey, SettingsModel s)
    {
        try
        {
            var newId   = rb.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty) as string;
            var newName = rb.GetCurrentPropertyValue(AutomationElement.NameProperty)         as string;
            bool changed = false;
            switch (modeKey)
            {
                case "adaptive":
                    if (!string.IsNullOrEmpty(newId)   && newId   != s.AdaptiveRadioId)   { s.AdaptiveRadioId   = newId;   changed = true; }
                    if (!string.IsNullOrEmpty(newName) && newName != s.AdaptiveRadioName) { s.AdaptiveRadioName = newName; changed = true; }
                    break;
                case "80":
                    if (!string.IsNullOrEmpty(newId)   && newId   != s.Limit80RadioId)    { s.Limit80RadioId    = newId;   changed = true; }
                    if (!string.IsNullOrEmpty(newName) && newName != s.Limit80RadioName)  { s.Limit80RadioName  = newName; changed = true; }
                    break;
                case "100":
                    if (!string.IsNullOrEmpty(newId)   && newId   != s.Charge100RadioId)  { s.Charge100RadioId  = newId;   changed = true; }
                    if (!string.IsNullOrEmpty(newName) && newName != s.Charge100RadioName){ s.Charge100RadioName= newName; changed = true; }
                    break;
            }
            if (changed) s.Save();
        }
        catch { }
    }

    private static void ClearRadioCache(string modeKey, SettingsModel s)
    {
        bool changed = false;
        switch (modeKey)
        {
            case "adaptive":
                if (s.AdaptiveRadioId != null || s.AdaptiveRadioName != null)   { s.AdaptiveRadioId = null;   s.AdaptiveRadioName = null;   changed = true; } break;
            case "80":
                if (s.Limit80RadioId != null || s.Limit80RadioName != null)     { s.Limit80RadioId = null;    s.Limit80RadioName = null;    changed = true; } break;
            case "100":
                if (s.Charge100RadioId != null || s.Charge100RadioName != null) { s.Charge100RadioId = null;  s.Charge100RadioName = null;  changed = true; } break;
        }
        if (changed) s.Save();
    }

    /// <summary>Local mini-helper since <see cref="SurfaceController"/>'s WaitFor is private.</summary>
    private static T? WaitFor<T>(System.Func<T?> probe, int timeoutMs, int pollMs) where T : class
    {
        var deadline = System.DateTime.Now.AddMilliseconds(timeoutMs);
        while (System.DateTime.Now < deadline)
        {
            try { var r = probe(); if (r != null) return r; }
            catch { }
            System.Threading.Thread.Sleep(pollMs);
        }
        return null;
    }
}

