import type { MouseEvent } from "react";
import { useCallback } from "react";
import * as Cs2Ui from "cs2/ui";
import {
    Button,
    Dropdown,
    DropdownToggle,
    Scrollable,
    Tooltip,
} from "cs2/ui";
import type {
    RoadAssetOption,
    RoadAssetRule,
    RuntimeDropdownItemProps,
} from "./custom-road-asset-match-types";
import type { VanillaCheckboxComponent, VanillaUiModules } from "./vanilla-ui";
import {
    getOptionName,
    getRuleSourceFeatureMask,
    hasSourceFeature,
    isSameOption,
    normalizeSourceFeatureMask,
    normalizeSourceFeatureMaskForSource,
    sourceFeatureMasks,
} from "./custom-road-asset-match-model";
import styles from "./custom-road-asset-match-panel.module.scss";

const DropdownItemComponent = (Cs2Ui as unknown as {
    DropdownItem: (props: RuntimeDropdownItemProps) => JSX.Element;
}).DropdownItem;

export const customRoadAssetIcons = {
    panel: "coui://uil/Standard/MagnifierRoad.svg",
    configure: "coui://uil/Standard/Gear.svg",
    collapse: "coui://uil/Standard/XClose.svg",
    delete: "coui://uil/Standard/Trash.svg",
    arrowRight: "coui://uil/Standard/ArrowRight.svg",
    tram: "Media/Game/Icons/DoubleTramTrack.svg",
    publicTransport: "Media/Game/Icons/DoublePublicTransportLane.svg",
    reverse: "coui://uil/Colored/Reset.svg",
};

type DropdownFieldProps = {
    label: string;
    action?: JSX.Element;
    children: JSX.Element;
};

export function DropdownField({ label, action, children }: DropdownFieldProps): JSX.Element {
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

export function RoadAssetDropdown({
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

export function SourceFeatureCheckboxRow({
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
                    icon={customRoadAssetIcons.tram}
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
                    icon={customRoadAssetIcons.publicTransport}
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
                    icon={customRoadAssetIcons.reverse}
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

export function RoadAssetRuleCard({ rule, vanilla, deleteTooltip, onSelect, onDelete }: RoadAssetRuleCardProps): JSX.Element {
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
                src={customRoadAssetIcons.delete}
                className={styles.ruleDeleteButton}
                tooltipLabel={deleteTooltip}
                onSelect={onDelete}
            />
        </div>
    );
}

export function joinClasses(...classes: Array<string | undefined>): string | undefined {
    const joined = classes.filter(Boolean).join(" ");
    return joined || undefined;
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
                <img src={customRoadAssetIcons.arrowRight} className={styles.ruleArrow} />
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
                <img src={customRoadAssetIcons.tram} className={styles.sourceFeatureIcon} />
            ) : null}
            {hasSourceFeature(normalizedFeatureMask, sourceFeatureMasks.publicTransport) ? (
                <img src={customRoadAssetIcons.publicTransport} className={styles.sourceFeatureIcon} />
            ) : null}
            {hasSourceFeature(normalizedFeatureMask, sourceFeatureMasks.reverse) ? (
                <img src={customRoadAssetIcons.reverse} className={styles.sourceFeatureIcon} />
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
    return option.icon || customRoadAssetIcons.panel;
}
