import type { RoadAssetOption, RoadAssetRule } from "./custom-road-asset-match-types";

export const sourceFeatureMasks = {
    none: 0,
    tram: 1,
    publicTransport: 2,
    reverse: 4,
};

export function parseJson<T>(value: string, fallback: T): T {
    if (!value) {
        return fallback;
    }

    try {
        return JSON.parse(value) as T;
    } catch {
        return fallback;
    }
}

export function isSameOption(left?: RoadAssetOption | null, right?: RoadAssetOption | null): boolean {
    return !!left && !!right && left.prefabName === right.prefabName;
}

export function findRuleForSource(
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

export function getOptionName(option: RoadAssetOption): string {
    return option.displayName || option.prefabName;
}

export function sortRoadAssetRules(rules: RoadAssetRule[]): RoadAssetRule[] {
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

export function sortRoadAssetOptions(options: RoadAssetOption[]): RoadAssetOption[] {
    return [...options].sort(compareRoadAssetOptions);
}

export function compareRoadAssetOptions(left: RoadAssetOption, right: RoadAssetOption): number {
    const leftLanes = getTotalRoadLanes(left);
    const rightLanes = getTotalRoadLanes(right);
    const totalLaneCompare = leftLanes - rightLanes;
    if (totalLaneCompare !== 0) {
        return totalLaneCompare;
    }

    return getOptionName(left).localeCompare(getOptionName(right), undefined, { numeric: true });
}

export function getRuleSourceFeatureMask(rule: RoadAssetRule): number {
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

export function normalizeSourceFeatureMask(sourceFeatureMask: number): number {
    return sourceFeatureMask & (
        sourceFeatureMasks.tram |
        sourceFeatureMasks.publicTransport |
        sourceFeatureMasks.reverse
    );
}

export function getDefaultSourceFeatureMask(source?: RoadAssetOption | null): number {
    return normalizeSourceFeatureMask(
        getMandatorySourceFeatureMask(source) |
        getForcedSourceFeatureMask(source)
    );
}

export function getDisabledSourceFeatureMask(source?: RoadAssetOption | null): number {
    return normalizeSourceFeatureMask(
        getMandatorySourceFeatureMask(source) |
        getForcedSourceFeatureMask(source)
    );
}

export function normalizeSourceFeatureMaskForSource(
    sourceFeatureMask: number,
    source?: RoadAssetOption | null
): number {
    const mandatoryFeatureMask = getDisabledSourceFeatureMask(source);
    const normalizedFeatureMask = normalizeSourceFeatureMask(sourceFeatureMask) | mandatoryFeatureMask;
    return canShowReverseSourceFeature(source, sourceFeatureMask)
        ? normalizedFeatureMask
        : normalizedFeatureMask & ~sourceFeatureMasks.reverse;
}

export function hasSourceFeature(sourceFeatureMask: number, featureMask: number): boolean {
    return (normalizeSourceFeatureMask(sourceFeatureMask) & featureMask) !== 0;
}

export function canShowReverseSourceFeature(
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

export function canShowTramSourceFeature(
    source?: RoadAssetOption | null,
    sourceFeatureMask = sourceFeatureMasks.none
): boolean {
    return source?.hasTram === true ||
           source?.canHaveTram === true ||
           hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.tram);
}

export function canShowPublicTransportSourceFeature(
    source?: RoadAssetOption | null,
    sourceFeatureMask = sourceFeatureMasks.none
): boolean {
    return source?.hasPublicTransport === true ||
           source?.canHavePublicTransport === true ||
           hasSourceFeature(sourceFeatureMask, sourceFeatureMasks.publicTransport);
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

function getForcedSourceFeatureMask(source?: RoadAssetOption | null): number {
    return shouldForceReverseSourceFeature(source)
        ? sourceFeatureMasks.reverse
        : sourceFeatureMasks.none;
}

function shouldForceReverseSourceFeature(source?: RoadAssetOption | null): boolean {
    return source?.hasReverseTargetCandidates === true &&
           source?.hasForwardTargetCandidates === false;
}

function getTotalRoadLanes(option: RoadAssetOption): number {
    if (typeof option.totalRoadLanes === "number" && option.totalRoadLanes > 0) {
        return option.totalRoadLanes;
    }

    if (typeof option.forwardRoadLanes === "number" &&
        typeof option.backwardRoadLanes === "number") {
        return option.forwardRoadLanes + option.backwardRoadLanes;
    }

    const match = /\blanes=(\d+)\/(\d+)/.exec(option.summary);
    if (!match) {
        return Number.MAX_SAFE_INTEGER;
    }

    const forward = Number.parseInt(match[1], 10);
    const backward = Number.parseInt(match[2], 10);
    return forward + backward;
}
