import { bindValue, call } from "cs2/api";
import type {
    MatchState,
    OptionListResponse,
    RoadAssetOption,
    SourceSelectionResponse,
} from "./custom-road-asset-match-types";
import { parseJson, sortRoadAssetOptions } from "./custom-road-asset-match-model";

export const toolEnabledBinding = bindValue<boolean>(__MOD_ID__, "ToolEnabled", false);
export const customRoadAssetMatchStateBinding = bindValue<string>(__MOD_ID__, "CustomRoadAssetMatchState", "{}");

export async function searchCustomRoadAssetSources(): Promise<RoadAssetOption[]> {
    const responseJson = await call<string>(__MOD_ID__, "SearchCustomRoadAssetSources", "");
    const response = parseJson<OptionListResponse>(responseJson, { items: [] });
    return sortRoadAssetOptions(response.items ?? []);
}

export async function selectCustomRoadAssetSource(
    source: RoadAssetOption,
    sourceFeatureMask: number
): Promise<SourceSelectionResponse> {
    const responseJson = await call<string>(
        __MOD_ID__,
        "SelectCustomRoadAssetSource",
        source.prefabName,
        String(sourceFeatureMask)
    );
    return parseJson<SourceSelectionResponse>(responseJson, { targets: [] });
}

export async function searchCustomRoadAssetTargets(
    source: RoadAssetOption,
    sourceFeatureMask: number
): Promise<RoadAssetOption[]> {
    const responseJson = await call<string>(
        __MOD_ID__,
        "SearchCustomRoadAssetTargets",
        source.prefabName,
        "",
        String(sourceFeatureMask)
    );
    const response = parseJson<SourceSelectionResponse>(responseJson, { targets: [] });
    return sortRoadAssetOptions(response.targets ?? []);
}

export async function setCustomRoadAssetMatch(
    source: RoadAssetOption,
    target: RoadAssetOption,
    sourceFeatureMask: number
): Promise<MatchState> {
    const responseJson = await call<string>(
        __MOD_ID__,
        "SetCustomRoadAssetMatch",
        source.prefabName,
        target.prefabName,
        String(sourceFeatureMask)
    );
    return parseJson<MatchState>(responseJson, { rules: [] });
}

export async function deleteCustomRoadAssetMatch(
    source: RoadAssetOption,
    sourceFeatureMask: number
): Promise<MatchState> {
    const responseJson = await call<string>(
        __MOD_ID__,
        "DeleteCustomRoadAssetMatch",
        source.prefabName,
        String(sourceFeatureMask)
    );
    return parseJson<MatchState>(responseJson, { rules: [] });
}
