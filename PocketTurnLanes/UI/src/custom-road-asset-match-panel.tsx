import { useCallback, useEffect, useMemo, useState } from "react";
import { trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import {
    Button,
    Panel,
    PanelSection,
    PanelSectionRow,
    Scrollable,
} from "cs2/ui";
import {
    customRoadAssetMatchStateBinding,
    deleteCustomRoadAssetMatch,
    searchCustomRoadAssetSources,
    searchCustomRoadAssetTargets,
    selectCustomRoadAssetSource,
    setCustomRoadAssetMatch,
    toolEnabledBinding,
} from "./custom-road-asset-match-api";
import {
    canShowPublicTransportSourceFeature,
    canShowReverseSourceFeature,
    canShowTramSourceFeature,
    findRuleForSource,
    getDefaultSourceFeatureMask,
    getDisabledSourceFeatureMask,
    getRuleSourceFeatureMask,
    hasSourceFeature,
    isSameOption,
    normalizeSourceFeatureMaskForSource,
    parseJson,
    sortRoadAssetOptions,
    sortRoadAssetRules,
    sourceFeatureMasks,
} from "./custom-road-asset-match-model";
import {
    customRoadAssetIcons,
    DropdownField,
    joinClasses,
    RoadAssetDropdown,
    RoadAssetRuleCard,
    SourceFeatureCheckboxRow,
} from "./custom-road-asset-match-components";
import type {
    MatchState,
    PanelProps,
    RoadAssetOption,
    RoadAssetRule,
} from "./custom-road-asset-match-types";
import styles from "./custom-road-asset-match-panel.module.scss";

export function CustomRoadAssetMatchPanel({ vanilla }: PanelProps): JSX.Element | null {
    const toolEnabled = useValue(toolEnabledBinding);
    const stateJson = useValue(customRoadAssetMatchStateBinding);
    const localization = useLocalization();
    const state = useMemo(() => parseJson<MatchState>(stateJson, { rules: [] }), [stateJson]);
    const rules = state.rules ?? [];
    const sortedRules = useMemo(() => sortRoadAssetRules(rules), [rules]);
    const [editing, setEditing] = useState(false);
    const [sourceOptions, setSourceOptions] = useState<RoadAssetOption[]>([]);
    const [targetOptions, setTargetOptions] = useState<RoadAssetOption[]>([]);
    const [selectedSource, setSelectedSource] = useState<RoadAssetOption | null>(null);
    const [selectedSourceFeatureMask, setSelectedSourceFeatureMask] = useState(sourceFeatureMasks.none);
    const [selectedTarget, setSelectedTarget] = useState<RoadAssetOption | null>(null);

    const t = useCallback((id: string, fallback: string) => {
        return localization.translate(`${__MOD_ID__}.UI.CustomRoadAssetMatches.${id}`, fallback) ?? fallback;
    }, [localization]);

    const loadSources = useCallback(async () => {
        setSourceOptions(await searchCustomRoadAssetSources());
    }, []);

    const loadTargets = useCallback(async (source: RoadAssetOption, sourceFeatureMask: number) => {
        const normalizedFeatureMask = normalizeSourceFeatureMaskForSource(sourceFeatureMask, source);
        setTargetOptions(await searchCustomRoadAssetTargets(source, normalizedFeatureMask));
    }, []);

    useEffect(() => {
        if (!toolEnabled) {
            setEditing(false);
            setSelectedSource(null);
            setSelectedSourceFeatureMask(sourceFeatureMasks.none);
            setSelectedTarget(null);
            setSourceOptions([]);
            setTargetOptions([]);
            return;
        }

        void loadSources();
    }, [loadSources, toolEnabled]);

    useEffect(() => {
        if (!toolEnabled || !editing) {
            return;
        }

        void loadSources();
    }, [editing, loadSources, toolEnabled]);

    useEffect(() => {
        if (!toolEnabled || !editing || !selectedSource) {
            return;
        }

        void loadTargets(selectedSource, selectedSourceFeatureMask);
    }, [editing, loadTargets, selectedSource, selectedSourceFeatureMask, toolEnabled]);

    useEffect(() => {
        const currentRule = findRuleForSource(rules, selectedSource, selectedSourceFeatureMask);
        if (!currentRule) {
            if (selectedTarget) {
                setSelectedTarget(null);
            }

            return;
        }

        if (!isSameOption(currentRule.target, selectedTarget)) {
            setSelectedTarget(currentRule.target);
        }
    }, [rules, selectedSource, selectedSourceFeatureMask, selectedTarget]);

    const toggleEditing = useCallback(() => {
        setEditing((current) => {
            const next = !current;
            if (next) {
                void loadSources();
            }

            return next;
        });
    }, [loadSources]);

    const selectSource = useCallback(async (
        source: RoadAssetOption,
        sourceFeatureMask = selectedSourceFeatureMask
    ) => {
        setSelectedSource(source);
        const normalizedFeatureMask = normalizeSourceFeatureMaskForSource(sourceFeatureMask, source);
        setSelectedSourceFeatureMask(normalizedFeatureMask);
        const existingRule = findRuleForSource(rules, source, normalizedFeatureMask);
        setSelectedTarget(existingRule?.target ?? null);

        const response = await selectCustomRoadAssetSource(source, normalizedFeatureMask);
        setTargetOptions(sortRoadAssetOptions(response.targets ?? []));
    }, [rules, selectedSourceFeatureMask]);

    const selectSourceFromDropdown = useCallback((source: RoadAssetOption) => {
        const sourceChanged = !isSameOption(source, selectedSource);
        const sourceFeatureMask = sourceChanged
            ? getDefaultSourceFeatureMask(source)
            : normalizeSourceFeatureMaskForSource(selectedSourceFeatureMask, source);
        void selectSource(source, sourceFeatureMask);
    }, [selectSource, selectedSource, selectedSourceFeatureMask]);

    const selectTarget = useCallback(async (target: RoadAssetOption) => {
        if (!selectedSource) {
            return;
        }

        setSelectedTarget(target);
        await setCustomRoadAssetMatch(selectedSource, target, selectedSourceFeatureMask);
    }, [selectedSource, selectedSourceFeatureMask]);

    const editRule = useCallback((rule: RoadAssetRule) => {
        const sourceFeatureMask = normalizeSourceFeatureMaskForSource(getRuleSourceFeatureMask(rule), rule.source);
        setEditing(true);
        setSelectedSourceFeatureMask(sourceFeatureMask);
        void selectSource(rule.source, sourceFeatureMask);
    }, [selectSource]);

    const deleteRule = useCallback(async (source: RoadAssetOption, sourceFeatureMask: number) => {
        const normalizedFeatureMask = normalizeSourceFeatureMaskForSource(sourceFeatureMask, source);
        await deleteCustomRoadAssetMatch(source, normalizedFeatureMask);
        if (selectedSource?.prefabName === source.prefabName &&
            selectedSourceFeatureMask === normalizedFeatureMask) {
            setSelectedTarget(null);
        }
    }, [selectedSource, selectedSourceFeatureMask]);

    const setSelectedSourceFeature = useCallback((featureMask: number, checked: boolean) => {
        if (hasSourceFeature(getDisabledSourceFeatureMask(selectedSource), featureMask)) {
            return;
        }

        setSelectedSourceFeatureMask((current) => normalizeSourceFeatureMaskForSource(
            checked ? current | featureMask : current & ~featureMask,
            selectedSource
        ));
    }, [selectedSource]);

    const closePanel = useCallback(() => {
        trigger(__MOD_ID__, "ToggleTool");
    }, []);

    if (!toolEnabled) {
        return null;
    }

    return (
        <Panel
            draggable={true}
            initialPosition={{ x: 0.72, y: 0.2 }}
            className={joinClasses(vanilla.selectedInfoPanelClasses?.selectedInfoPanel, styles.panelBounds)}
            theme={vanilla.selectedInfoPanelTheme ?? vanilla.panelTheme}
            onClose={closePanel}
            header={t("Title", "Road Asset Matches")}
        >
            {!editing ? (
                <PanelSectionRow
                    disableFocus
                    right={
                        <Button
                            variant="icon"
                            src={customRoadAssetIcons.configure}
                            tooltipLabel={t("EditMatches", "Edit matches")}
                            onSelect={toggleEditing}
                        />
                    }
                />
            ) : (
                <div className={styles.editorList}>
                    <DropdownField
                        label={t("Source", "Source")}
                        action={
                            <Button
                                variant="icon"
                                src={customRoadAssetIcons.collapse}
                                className={styles.editorCollapseButton}
                                tooltipLabel={t("CloseEditor", "Close editor")}
                                onSelect={toggleEditing}
                            />
                        }
                    >
                        <div className={styles.sourceEditorControl}>
                            <RoadAssetDropdown
                                options={sourceOptions}
                                selected={selectedSource}
                                placeholder={t("SourcePlaceholder", "Select source road")}
                                emptyText={t("NoSources", "No matching source roads.")}
                                theme={vanilla.dropdownTheme}
                                onSelect={selectSourceFromDropdown}
                            />
                            {selectedSource ? (
                                <SourceFeatureCheckboxRow
                                    vanilla={vanilla}
                                    sourceFeatureMask={selectedSourceFeatureMask}
                                    disabledFeatureMask={getDisabledSourceFeatureMask(selectedSource)}
                                    showTram={canShowTramSourceFeature(
                                        selectedSource,
                                        selectedSourceFeatureMask
                                    )}
                                    showPublicTransport={canShowPublicTransportSourceFeature(
                                        selectedSource,
                                        selectedSourceFeatureMask
                                    )}
                                    tramTooltip={t("SourceFeatureTramTooltip", "Tram tracks")}
                                    publicTransportTooltip={t(
                                        "SourceFeaturePublicTransportTooltip",
                                        "Public transport lanes"
                                    )}
                                    reverseTooltip={t("SourceFeatureReverseTooltip", "Reverse direction")}
                                    showReverse={canShowReverseSourceFeature(
                                        selectedSource,
                                        selectedSourceFeatureMask
                                    )}
                                    onCheckedChange={setSelectedSourceFeature}
                                />
                            ) : null}
                        </div>
                    </DropdownField>
                    <DropdownField label={t("Target", "Target")}>
                        <RoadAssetDropdown
                            options={targetOptions}
                            selected={selectedTarget}
                            placeholder={selectedSource
                                ? t("TargetPlaceholder", "Select compatible target")
                                : t("TargetNeedsSource", "Select a source first")}
                            emptyText={selectedSource
                                ? t("NoTargets", "No compatible target roads.")
                                : t("TargetNeedsSource", "Select a source first")}
                            disabled={!selectedSource}
                            theme={vanilla.dropdownTheme}
                            onSelect={(option) => void selectTarget(option)}
                        />
                    </DropdownField>
                </div>
            )}

            {sortedRules.length === 0 ? (
                <PanelSection>
                    <PanelSectionRow left={t("NoRules", "No custom rules. Default matching rules are used.")} disableFocus />
                </PanelSection>
            ) : (
                <Scrollable
                    vertical
                    trackVisibility="scrollable"
                    className={joinClasses(
                        vanilla.selectedInfoPanelClasses?.scrollable,
                        vanilla.activeInfoviewPanelClasses?.infomodesPanel,
                        styles.ruleList
                    )}
                >
                    <div className={styles.ruleListLabel}>
                        {t("Rules", "Rules")}
                    </div>
                    {sortedRules.map((rule) => (
                        <RoadAssetRuleCard
                            key={`${rule.source.prefabName}:${normalizeSourceFeatureMaskForSource(
                                getRuleSourceFeatureMask(rule),
                                rule.source
                            )}`}
                            rule={rule}
                            vanilla={vanilla}
                            deleteTooltip={t("DeleteMatch", "Delete match")}
                            onSelect={() => editRule(rule)}
                            onDelete={() => void deleteRule(rule.source, getRuleSourceFeatureMask(rule))}
                        />
                    ))}
                </Scrollable>
            )}
        </Panel>
    );
}
