import { ReactNode, useCallback, useEffect, useMemo, useState } from "react";
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
} from "cs2/ui";
import { VanillaUiModules } from "./vanilla-ui";
import styles from "./custom-road-asset-match-panel.module.scss";

type RoadAssetOption = {
    prefabName: string;
    displayName: string;
    icon: string;
    summary: string;
    isDlc: boolean;
    contentDetail: string;
};

type RoadAssetRule = {
    source: RoadAssetOption;
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

function findRuleForSource(rules: RoadAssetRule[], source?: RoadAssetOption | null): RoadAssetRule | undefined {
    if (!source) {
        return undefined;
    }

    return rules.find((rule) => rule.source.prefabName === source.prefabName);
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
    const [selectedTarget, setSelectedTarget] = useState<RoadAssetOption | null>(null);

    const t = useCallback((id: string, fallback: string) => {
        return localization.translate(`${__MOD_ID__}.UI.CustomRoadAssetMatches.${id}`, fallback) ?? fallback;
    }, [localization]);

    const loadSources = useCallback(async () => {
        const responseJson = await call<string>(__MOD_ID__, "SearchCustomRoadAssetSources", "");
        const response = parseJson<OptionListResponse>(responseJson, { items: [] });
        setSourceOptions(sortRoadAssetOptions(response.items ?? []));
    }, []);

    const loadTargets = useCallback(async (source: RoadAssetOption) => {
        const responseJson = await call<string>(__MOD_ID__, "SearchCustomRoadAssetTargets", source.prefabName, "");
        const response = parseJson<SourceSelectionResponse>(responseJson, { targets: [] });
        setTargetOptions(sortRoadAssetOptions(response.targets ?? []));
    }, []);

    useEffect(() => {
        if (!toolEnabled) {
            setEditing(false);
            setSelectedSource(null);
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

        void loadTargets(selectedSource);
    }, [editing, loadTargets, selectedSource, toolEnabled]);

    useEffect(() => {
        const currentRule = findRuleForSource(rules, selectedSource);
        if (currentRule && !isSameOption(currentRule.target, selectedTarget)) {
            setSelectedTarget(currentRule.target);
        }
    }, [rules, selectedSource, selectedTarget]);

    const toggleEditing = useCallback(() => {
        setEditing((current) => {
            const next = !current;
            if (next) {
                void loadSources();
            }

            return next;
        });
    }, [loadSources]);

    const selectSource = useCallback(async (source: RoadAssetOption) => {
        setSelectedSource(source);
        const existingRule = findRuleForSource(rules, source);
        setSelectedTarget(existingRule?.target ?? null);

        const responseJson = await call<string>(__MOD_ID__, "SelectCustomRoadAssetSource", source.prefabName);
        const response = parseJson<SourceSelectionResponse>(responseJson, { targets: [] });
        setTargetOptions(sortRoadAssetOptions(response.targets ?? []));
    }, [rules]);

    const selectTarget = useCallback(async (target: RoadAssetOption) => {
        if (!selectedSource) {
            return;
        }

        setSelectedTarget(target);
        const responseJson = await call<string>(
            __MOD_ID__,
            "SetCustomRoadAssetMatch",
            selectedSource.prefabName,
            target.prefabName
        );
        parseJson<MatchState>(responseJson, { rules: [] });
    }, [selectedSource]);

    const editRule = useCallback((rule: RoadAssetRule) => {
        setEditing(true);
        void selectSource(rule.source);
    }, [selectSource]);

    const deleteRule = useCallback(async (sourcePrefabName: string) => {
        const responseJson = await call<string>(__MOD_ID__, "DeleteCustomRoadAssetMatch", sourcePrefabName);
        parseJson<MatchState>(responseJson, { rules: [] });
        if (selectedSource?.prefabName === sourcePrefabName) {
            setSelectedTarget(null);
        }
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
                        <RoadAssetDropdown
                            options={sourceOptions}
                            selected={selectedSource}
                            placeholder={t("SourcePlaceholder", "Select source road")}
                            emptyText={t("NoSources", "No matching source roads.")}
                            theme={vanilla.dropdownTheme}
                            onSelect={(option) => void selectSource(option)}
                        />
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
                            key={rule.source.prefabName}
                            rule={rule}
                            vanilla={vanilla}
                            deleteTooltip={t("DeleteMatch", "Delete match")}
                            onSelect={() => editRule(rule)}
                            onDelete={() => void deleteRule(rule.source.prefabName)}
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
            <div className={styles.ruleTargetLine}>
                <img src={icons.arrowRight} className={styles.ruleArrow} />
                <RoadAssetRuleEndpoint option={rule.target} className={styles.ruleTargetEndpoint} />
            </div>
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

function joinClasses(...classes: Array<string | undefined>): string | undefined {
    const joined = classes.filter(Boolean).join(" ");
    return joined || undefined;
}
