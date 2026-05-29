import { ModRegistrar } from "cs2/modding";
import { DedicatedTurnLanesButton } from "mods/dedicated-turn-lanes-button";

const register: ModRegistrar = (moduleRegistry) => {

    moduleRegistry.append("GameTopLeft", DedicatedTurnLanesButton);
}

export default register;
