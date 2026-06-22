import type { MouseEvent, ReactNode } from "react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { bindValue, call, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import * as Cs2Ui from "cs2/ui";
import {
    Button,
    Dropdown,
    DropdownToggle,
    Panel,
    PanelSection,
    PanelSectionRow,
    Scrollable,
    Tooltip,
} from "cs2/ui";
import type { VanillaCheckboxComponent, VanillaUiModules } from "./vanilla-ui";
import styles from "./custom-road-asset-match-panel.module.scss";

type RoadAssetOption = {
    prefabName: string;
    displayName: string;
    icon: string;
    summary: string;
    isDlc: boolean;
    contentDetail: string;
    hasTram?: boolean;
    hasPublicTransport?: boolean;
    canHaveTram?: boolean;
    canHavePublicTransport?: boolean;
    hasAsymmetricRoadLanes?: boolean;
    hasReverseSourceSide?: boolean;
    hasForwardTargetCandidates?: boolean;
    hasReverseTargetCandidates?: boolean;
};

type RoadAssetRule = {
    source: RoadAssetOption;
    sourceFeatureMask?: number;
    sourceHasTram?: boolean;
    sourceHasPublicTransport?: boolean;
    sourceIsReversed?: boolean;
    target: RoadAssetOption;
};

type MatchState = {
    schemaVersion?: number;
    action?: string;
    message?: string;
    cleanedInvalidRules?: number;
    ruleCount?: number;
    rules?: RoadAssetRule[];
};

type OptionListResponse = {
    items?: RoadAssetOption[];
    message?: string;
    cleanedInvalidRules?: number;
};

type SourceSelectionResponse = {
    success?: boolean;
    source?: RoadAssetOption | null;
    targets?: RoadAssetOption[];
    message?: string;
    cleanedInvalidRules?: number;
};

type PanelProps = {
    vanilla: VanillaUiModules;
};

type RuntimeDropdownItemProps = {
    value: string;
    selected?: boolean;
    closeOnSelect?: boolean;
    theme?: VanillaUiModules["dropdownTheme"];
    onChange?: (value: string) => void;
    children?: ReactNode;
};

const DropdownItemComponent = (Cs2Ui as unknown as {
    DropdownItem: (props: RuntimeDropdownItemProps) => JSX.Element;
}).DropdownItem;

const toolEnabledBinding = bindValue<boolean>(__MOD_ID__, "ToolEnabled", false);
const customRoadAssetMatchStateBinding = bindValue<string>(__MOD_ID__, "CustomRoadAssetMatchState", "{}");

const icons = {
    panel: "coui://uil/Standard/MagnifierRoad.svg",
    configure: "coui://uil/Standard/Gear.svg",
    collapse: "coui://uil/Standard/XClose.svg",
    close: "coui://uil/Standard/XClose.svg",
    delete: "coui://uil/Standard/Trash.svg",
    arrowRight: "coui://uil/Standard/ArrowRight.svg",
    tram: "Media/Game/Icons/DoubleTramTrack.svg",
    publicTransport: "Media/Game/Icons/DoublePublicTransportLane.svg",
    reverse: "coui://uil/Colored/Reset.svg",
};

const sourceFeatureMasks = {
    none: 0,
    tram: 1,
    publicTransport: 2,
    reverse: 4,
};

function parseJson<T>(value: string, fallback: T): T {
    if (!value) {
        return fallback;
    }

    try {
        return JSON.parse(value) as T;
    } catch {
        return fallback;
    }
}

function isSameOption(left?: RoadAssetOption | null, right?: RoadAssetOption | null): boolean {
    return !!left && !!right && left.prefabName === right.prefabName;
}

function findRuleForSource(
    rules: RoadAssetRule[],
    source?: RoadAssetOption | null,
    sourceFeatureMask = sourceFeatureMasks.none
): RoadAssetRule | undefined {
    if (!source) {
        return undefined;
    }

    const normalizedFeatureMask = normalizeSourceFeatureMaskForSource(sourceFeatureMask, source);
    return rules.find((rule) =>
        rule.source.prefabName === source.prefabName &&
        normalizeSourceFeatureMaskForSource(getRuleSourceFeatureMask(rule), rule.source) === normalizedFeatureMask
    );
}

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
        const responseJson = await call<string>(__MOD_ID__, "SearchCustomRoadAssetSources", "");
        const response = parseJson<OptionListResponse>(responseJson, { items: [] });
        setSourceOptions(sortRoadAssetOptions(response.items ?? []));
    }, []);

    const loadTargets = useCallback(async (source: RoadAssetOption, sourceFeatureMask: number) => {
        const normalizedFeatureMask = normalizeSourceFeatureMaskForSource(sourceFeatureMask, source);
        const responseJson = await call<string>(
            __MOD_ID__,
            "SearchCustomRoadAssetTargets",
            source.prefabName,
            "",
            String(normalizedFeatureMask)
        );
        const response = parseJson<SourceSelectionResponse>(responseJson, { targets: [] });
        setTargetOptions(sortRoadAssetOptions(response.targets ?? []));
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

        const responseJson = await call<string>(
            __MOD_ID__,
            "SelectCustomRoadAssetSource",
            source.prefabName,
            String(normalizedFeatureMask)
        );
        const response = parseJson<SourceSelectionResponse>(responseJson, { targets: [] });
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
        const responseJson = await call<string>(
            __MOD_ID__,
            "SetCustomRoadAssetMatch",
            selectedSource.prefabName,
            target.prefabName,
            String(selectedSourceFeatureMask)
        );
        parseJson<MatchState>(responseJson, { rules: [] });
    }, [selectedSource, selectedSourceFeatureMask]);

    const editRule = useCallback((rule: RoadAssetRule) => {
        const sourceFeatureMask = normalizeSourceFeatureMaskForSource(getRuleSourceFeatureMask(rule), rule.source);
        setEditing(true);
        setSelectedSourceFeatureMask(sourceFeatureMask);
        void selectSource(rule.source, sourceFeatureMask);
    }, [selectSource]);

    const deleteRule = useCallback(async (source: RoadAssetOption, sourceFeatureMask: number) => {
        const normalizedFeatureMask = normalizeSourceFeatureMaskForSource(sourceFeatureMask, source);
        const responseJson = await call<string>(
            __MOD_ID__,
            "DeleteCustomRoadAssetMatch",
            source.prefabName,
            String(normalizedFeatureMask)
        );
        parseJson<MatchState>(responseJson, { rules: [] });
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
                            src={icons.configure}
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
                                src={icons.collapse}
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

type DropdownFieldProps = {
    label: string;
    action?: ReactNode;
    children: ReactNode;
};

function DropdownField({ label, action, children }: DropdownFieldProps): JSX.Element {
    return (
        <div className={styles.editorField}>
            <div className={styles.editorFieldHeader}>
                <div className={styles.editorLabel}>
                    {label}
                </div>
                <div className={styles.editorAction}>
                    {action}
                </div>
            </div>
            <div className={styles.editorFieldControl}>
                {children}
            </div>
        </div>
    );
}

type RoadAssetDropdownProps = {
    options: RoadAssetOption[];
    selected: RoadAssetOption | null;
    placeholder: string;
    emptyText: string;
    theme?: VanillaUiModules["dropdownTheme"];
    disabled?: boolean;
    onSelect: (option: RoadAssetOption) => void;
};

function RoadAssetDropdown({
    options,
    selected,
    placeholder,
    emptyText,
    theme,
    disabled,
    onSelect,
}: RoadAssetDropdownProps): JSX.Element {
    const selectedText = selected ? getOptionName(selected) : placeholder;
    const selectByPrefabName = (prefabName: string) => {
        const option = options.find((candidate) => candidate.prefabName === prefabName);
        if (option) {
            onSelect(option);
        }
    };

    return (
        <Dropdown
            alignment="left"
            theme={theme}
            content={
                <Scrollable vertical className={styles.dropdownList}>
                    {options.length === 0 ? (
                        <div className={styles.dropdownEmpty}>{emptyText}</div>
                    ) : (
                        options.map((option) => (
                            <DropdownItemComponent
                                key={option.prefabName}
                                value={option.prefabName}
                                selected={isSameOption(option, selected)}
                                theme={theme}
                                closeOnSelect={true}
                                onChange={selectByPrefabName}
                            >
                                <RoadAssetOptionContent option={option} />
                            </DropdownItemComponent>
                        ))
                    )}
                </Scrollable>
            }
        >
            <DropdownToggle
                className={styles.dropdownToggle}
                disabled={disabled}
                tooltipLabel={selectedText}
            >
                <div className={styles.dropdownToggleLine}>
                    {selected ? <RoadAssetOptionContent option={selected} /> : <RoadAssetName name={selectedText} />}
                </div>
            </DropdownToggle>
        </Dropdown>
    );
}

function getOptionName(option: RoadAssetOption): string {
    return option.displayName || option.prefabName;
}

function RoadAssetOptionContent({ option }: { option: RoadAssetOption }): JSX.Element {
    return (
        <div className={styles.roadAssetOption}>
            <img src={getOptionIcon(option)} className={styles.roadAssetIcon} />
            <RoadAssetName name={getOptionName(option)} />
        </div>
    );
}

function RoadAssetName({ name }: { name: string }): JSX.Element {
    return <span className={styles.roadAssetName}>{name}</span>;
}

type SourceFeatureCheckboxRowProps = {
    vanilla: VanillaUiModules;
    sourceFeatureMask: number;
    disabledFeatureMask: number;
    showTram: boolean;
    showPublicTransport: boolean;
    tramTooltip: string;
    publicTransportTooltip: string;
    reverseTooltip: string;
    showReverse: boolean;
    onCheckedChange: (featureMask: number, checked: boolean) => void;
};

function SourceFeatureCheckboxRow({
    vanilla,
    sourceFeatureMask,
    disabledFeatureMask,
    showTram,
    showPublicTransport,
    tramTooltip,
    publicTransportTooltip,
    reverseTooltip,
    showReverse,
    onCheckedChange,
}: SourceFeatureCheckboxRowProps): JSX.Element | null {
    const CheckboxComponent = vanilla.checkboxComponent;
    const tramChecked = hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.tram);
    const publicTransportChecked = hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.publicTransport);
    const reverseChecked = hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.reverse);
    const tramDisabled = hasSourceFeature(disabledFeatureMask, sourceFeatureMasks.tram);
    const publicTransportDisabled = hasSourceFeature(disabledFeatureMask, sourceFeatureMasks.publicTransport);
    const reverseDisabled = hasSourceFeature(disabledFeatureMask, sourceFeatureMasks.reverse);
    if (!CheckboxComponent) {
        return null;
    }

    if (!showTram && !showPublicTransport && !showReverse) {
        return null;
    }

    return (
        <div className={styles.sourceFeatureCheckboxRow}>
            {showTram ? (
                <SourceFeatureCheckbox
                    CheckboxComponent={CheckboxComponent}
                    checkboxTheme={vanilla.checkboxTheme}
                    checked={tramChecked}
                    disabled={tramDisabled}
                    icon={icons.tram}
                    tooltip={tramTooltip}
                    onCheckedChange={(checked) => onCheckedChange(sourceFeatureMasks.tram, checked)}
                />
            ) : null}
            {showPublicTransport ? (
                <SourceFeatureCheckbox
                    CheckboxComponent={CheckboxComponent}
                    checkboxTheme={vanilla.checkboxTheme}
                    checked={publicTransportChecked}
                    disabled={publicTransportDisabled}
                    icon={icons.publicTransport}
                    tooltip={publicTransportTooltip}
                    onCheckedChange={(checked) => onCheckedChange(sourceFeatureMasks.publicTransport, checked)}
                />
            ) : null}
            {showReverse ? (
                <SourceFeatureCheckbox
                    CheckboxComponent={CheckboxComponent}
                    checkboxTheme={vanilla.checkboxTheme}
                    checked={reverseChecked}
                    disabled={reverseDisabled}
                    icon={icons.reverse}
                    tooltip={reverseTooltip}
                    onCheckedChange={(checked) => onCheckedChange(sourceFeatureMasks.reverse, checked)}
                />
            ) : null}
        </div>
    );
}

type SourceFeatureCheckboxProps = {
    CheckboxComponent: VanillaCheckboxComponent;
    checkboxTheme?: Record<string, string>;
    checked: boolean;
    disabled?: boolean;
    icon: string;
    tooltip: string;
    onCheckedChange: (checked: boolean) => void;
};

function SourceFeatureCheckbox({
    CheckboxComponent,
    checkboxTheme,
    checked,
    disabled,
    icon,
    tooltip,
    onCheckedChange,
}: SourceFeatureCheckboxProps): JSX.Element {
    const handleCheckboxChange = useCallback((nextChecked: boolean) => {
        if (disabled) {
            return;
        }

        onCheckedChange(nextChecked);
    }, [disabled, onCheckedChange]);

    const handleLabelClick = useCallback((event: MouseEvent<HTMLLabelElement>) => {
        event.preventDefault();
        if (disabled) {
            return;
        }

        onCheckedChange(!checked);
    }, [checked, disabled, onCheckedChange]);

    return (
        <Tooltip tooltip={tooltip}>
            <label className={styles.sourceFeatureCheckbox} onClick={handleLabelClick}>
                <img src={icon} className={styles.sourceFeatureCheckboxIcon} />
                <CheckboxComponent
                    checked={checked}
                    disabled={disabled}
                    theme={checkboxTheme}
                    className={styles.sourceFeatureCheckboxControl}
                    onChange={handleCheckboxChange}
                />
            </label>
        </Tooltip>
    );
}

type RoadAssetRuleCardProps = {
    rule: RoadAssetRule;
    vanilla: VanillaUiModules;
    deleteTooltip: string;
    onSelect: () => void;
    onDelete: () => void;
};

function RoadAssetRuleCard({ rule, vanilla, deleteTooltip, onSelect, onDelete }: RoadAssetRuleCardProps): JSX.Element {
    return (
        <div className={styles.ruleCardShell}>
            <Button
                as="div"
                disableHint
                theme={vanilla.transparentButtonTheme}
                onSelect={onSelect}
                className={joinClasses(
                    vanilla.infomodeItemClasses?.infomodeItem,
                    vanilla.infomodeItemClasses?.active,
                    styles.ruleCard
                )}
            >
                <div className={joinClasses(vanilla.infomodeItemClasses?.header, styles.ruleCardHeader)}>
                    <div
                        className={joinClasses(
                            vanilla.infomodeItemClasses?.title,
                            vanilla.infomodeItemClasses?.activeOpacity,
                            styles.ruleCardTitle
                        )}
                    >
                        <RoadAssetRuleMatch rule={rule} />
                    </div>
                    <div className={joinClasses(vanilla.infomodeItemClasses?.type, styles.ruleCardActionSlot)} />
                </div>
            </Button>
            <Button
                variant="icon"
                src={icons.delete}
                className={styles.ruleDeleteButton}
                tooltipLabel={deleteTooltip}
                onSelect={onDelete}
            />
        </div>
    );
}

function RoadAssetRuleMatch({ rule }: { rule: RoadAssetRule }): JSX.Element {
    return (
        <div className={styles.ruleMatch}>
            <div className={styles.ruleSourceLine}>
                <RoadAssetRuleEndpoint option={rule.source} className={styles.ruleSourceEndpoint} />
            </div>
            <SourceFeatureIconRow
                sourceFeatureMask={normalizeSourceFeatureMaskForSource(getRuleSourceFeatureMask(rule), rule.source)}
                className={styles.ruleFeatureLine}
            />
            <div className={styles.ruleTargetLine}>
                <img src={icons.arrowRight} className={styles.ruleArrow} />
                <RoadAssetRuleEndpoint option={rule.target} className={styles.ruleTargetEndpoint} />
            </div>
        </div>
    );
}

function SourceFeatureIconRow({
    sourceFeatureMask,
    className,
}: {
    sourceFeatureMask: number;
    className?: string;
}): JSX.Element | null {
    const normalizedFeatureMask = normalizeSourceFeatureMask(sourceFeatureMask);
    if (normalizedFeatureMask === sourceFeatureMasks.none) {
        return null;
    }

    return (
        <div className={joinClasses(styles.sourceFeatureIconRow, className)}>
            {hasSourceFeature(normalizedFeatureMask, sourceFeatureMasks.tram) ? (
                <img src={icons.tram} className={styles.sourceFeatureIcon} />
            ) : null}
            {hasSourceFeature(normalizedFeatureMask, sourceFeatureMasks.publicTransport) ? (
                <img src={icons.publicTransport} className={styles.sourceFeatureIcon} />
            ) : null}
            {hasSourceFeature(normalizedFeatureMask, sourceFeatureMasks.reverse) ? (
                <img src={icons.reverse} className={styles.sourceFeatureIcon} />
            ) : null}
        </div>
    );
}

function RoadAssetRuleEndpoint({ option, className }: { option: RoadAssetOption; className?: string }): JSX.Element {
    return (
        <div className={joinClasses(styles.ruleEndpoint, className)}>
            <img src={getOptionIcon(option)} className={styles.ruleEndpointIcon} />
            <span className={styles.ruleEndpointName}>{getOptionName(option)}</span>
        </div>
    );
}

function getOptionIcon(option: RoadAssetOption): string {
    return option.icon || icons.panel;
}

function sortRoadAssetRules(rules: RoadAssetRule[]): RoadAssetRule[] {
    return [...rules].sort((left, right) => {
        const sourceCompare = compareRoadAssetOptions(left.source, right.source);
        if (sourceCompare !== 0) {
            return sourceCompare;
        }

        const sourceFeatureCompare =
            normalizeSourceFeatureMaskForSource(getRuleSourceFeatureMask(left), left.source) -
            normalizeSourceFeatureMaskForSource(getRuleSourceFeatureMask(right), right.source);
        if (sourceFeatureCompare !== 0) {
            return sourceFeatureCompare;
        }

        return compareRoadAssetOptions(left.target, right.target);
    });
}

function sortRoadAssetOptions(options: RoadAssetOption[]): RoadAssetOption[] {
    return [...options].sort(compareRoadAssetOptions);
}

function compareRoadAssetOptions(left: RoadAssetOption, right: RoadAssetOption): number {
    const leftLanes = getLaneSortInfo(left);
    const rightLanes = getLaneSortInfo(right);
    const totalLaneCompare = leftLanes.total - rightLanes.total;
    if (totalLaneCompare !== 0) {
        return totalLaneCompare;
    }

    return getOptionName(left).localeCompare(getOptionName(right), undefined, { numeric: true });
}

function getLaneSortInfo(option: RoadAssetOption): { total: number } {
    const match = /\blanes=(\d+)\/(\d+)/.exec(option.summary);
    if (!match) {
        return { total: Number.MAX_SAFE_INTEGER };
    }

    const forward = Number.parseInt(match[1], 10);
    const backward = Number.parseInt(match[2], 10);
    return { total: forward + backward };
}

function getRuleSourceFeatureMask(rule: RoadAssetRule): number {
    if (typeof rule.sourceFeatureMask === "number") {
        return normalizeSourceFeatureMask(rule.sourceFeatureMask);
    }

    let sourceFeatureMask = sourceFeatureMasks.none;
    if (rule.sourceHasTram) {
        sourceFeatureMask |= sourceFeatureMasks.tram;
    }

    if (rule.sourceHasPublicTransport) {
        sourceFeatureMask |= sourceFeatureMasks.publicTransport;
    }

    if (rule.sourceIsReversed) {
        sourceFeatureMask |= sourceFeatureMasks.reverse;
    }

    return normalizeSourceFeatureMask(sourceFeatureMask);
}

function normalizeSourceFeatureMask(sourceFeatureMask: number): number {
    return sourceFeatureMask & (
        sourceFeatureMasks.tram |
        sourceFeatureMasks.publicTransport |
        sourceFeatureMasks.reverse
    );
}

function getMandatorySourceFeatureMask(source?: RoadAssetOption | null): number {
    let sourceFeatureMask = sourceFeatureMasks.none;
    if (source?.hasTram === true) {
        sourceFeatureMask |= sourceFeatureMasks.tram;
    }

    if (source?.hasPublicTransport === true) {
        sourceFeatureMask |= sourceFeatureMasks.publicTransport;
    }

    return sourceFeatureMask;
}

function getDefaultSourceFeatureMask(source?: RoadAssetOption | null): number {
    return normalizeSourceFeatureMask(
        getMandatorySourceFeatureMask(source) |
        getForcedSourceFeatureMask(source)
    );
}

function getDisabledSourceFeatureMask(source?: RoadAssetOption | null): number {
    return normalizeSourceFeatureMask(
        getMandatorySourceFeatureMask(source) |
        getForcedSourceFeatureMask(source)
    );
}

function getForcedSourceFeatureMask(source?: RoadAssetOption | null): number {
    return shouldForceReverseSourceFeature(source)
        ? sourceFeatureMasks.reverse
        : sourceFeatureMasks.none;
}

function shouldForceReverseSourceFeature(source?: RoadAssetOption | null): boolean {
    return source?.hasReverseTargetCandidates === true &&
           source?.hasForwardTargetCandidates === false;
}

function normalizeSourceFeatureMaskForSource(
    sourceFeatureMask: number,
    source?: RoadAssetOption | null
): number {
    const mandatoryFeatureMask = getDisabledSourceFeatureMask(source);
    const normalizedFeatureMask = normalizeSourceFeatureMask(sourceFeatureMask) | mandatoryFeatureMask;
    const allowedFeatureMask = canShowReverseSourceFeature(source, sourceFeatureMask)
        ? normalizedFeatureMask
        : normalizedFeatureMask & ~sourceFeatureMasks.reverse;
    return allowedFeatureMask;
}

function hasSourceFeature(sourceFeatureMask: number, featureMask: number): boolean {
    return (normalizeSourceFeatureMask(sourceFeatureMask) & featureMask) !== 0;
}

function canShowReverseSourceFeature(
    source?: RoadAssetOption | null,
    sourceFeatureMask = sourceFeatureMasks.none
): boolean {
    if (source?.hasReverseTargetCandidates === true) {
        return true;
    }

    if (source?.hasForwardTargetCandidates === true ||
        source?.hasReverseTargetCandidates === false) {
        return false;
    }

    return hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.reverse);
}

function canShowTramSourceFeature(
    source?: RoadAssetOption | null,
    sourceFeatureMask = sourceFeatureMasks.none
): boolean {
    return source?.hasTram === true ||
           source?.canHaveTram === true ||
           hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.tram);
}

function canShowPublicTransportSourceFeature(
    source?: RoadAssetOption | null,
    sourceFeatureMask = sourceFeatureMasks.none
): boolean {
    return source?.hasPublicTransport === true ||
           source?.canHavePublicTransport === true ||
           hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.publicTransport);
}

function joinClasses(...classes: Array<string | undefined>): string | undefined {
    const joined = classes.filter(Boolean).join(" ");
    return joined || undefined;
}
