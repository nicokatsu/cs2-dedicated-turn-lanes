import { ModRegistrar } from "cs2/modding";
import { CustomRoadAssetMatchPanel } from "./custom-road-asset-match-panel";
import { resolveVanillaUiModules } from "./vanilla-ui";
import "images/dedicated-turn-lanes-icon-default.svg";

const register: ModRegistrar = (moduleRegistry) => {
    const vanilla = resolveVanillaUiModules(moduleRegistry);
    moduleRegistry.append("Game", () => <CustomRoadAssetMatchPanel vanilla={vanilla} />);
}

export default register;
