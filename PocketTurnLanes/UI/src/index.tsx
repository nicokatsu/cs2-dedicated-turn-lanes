import { ModRegistrar } from "cs2/modding";
import { PocketTurnLanesButton } from "mods/pocket-turn-lanes-button";

const register: ModRegistrar = (moduleRegistry) => {

    moduleRegistry.append("GameTopLeft", PocketTurnLanesButton);
}

export default register;
