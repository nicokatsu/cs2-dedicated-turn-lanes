import type { ReactNode } from "react";
import type { VanillaUiModules } from "./vanilla-ui";

export type RoadAssetOption = {
    prefabName: string;
    displayName: string;
    icon: string;
    summary: string;
    forwardRoadLanes?: number;
    backwardRoadLanes?: number;
    totalRoadLanes?: number;
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

export type RoadAssetRule = {
    source: RoadAssetOption;
    sourceFeatureMask?: number;
    sourceHasTram?: boolean;
    sourceHasPublicTransport?: boolean;
    sourceIsReversed?: boolean;
    target: RoadAssetOption;
};

export type MatchState = {
    schemaVersion?: number;
    action?: string;
    message?: string;
    cleanedInvalidRules?: number;
    ruleCount?: number;
    rules?: RoadAssetRule[];
};

export type OptionListResponse = {
    items?: RoadAssetOption[];
    message?: string;
    cleanedInvalidRules?: number;
};

export type SourceSelectionResponse = {
    success?: boolean;
    source?: RoadAssetOption | null;
    targets?: RoadAssetOption[];
    message?: string;
    cleanedInvalidRules?: number;
};

export type PanelProps = {
    vanilla: VanillaUiModules;
};

export type RuntimeDropdownItemProps = {
    value: string;
    selected?: boolean;
    closeOnSelect?: boolean;
    theme?: VanillaUiModules["dropdownTheme"];
    onChange?: (value: string) => void;
    children?: ReactNode;
};
