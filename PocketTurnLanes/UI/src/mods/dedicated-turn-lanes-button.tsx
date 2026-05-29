import { bindValue, trigger, useValue } from "cs2/api";
import { getModule } from "cs2/modding";
import { Button } from "cs2/ui";

const bindingGroup = "DedicatedTurnLanes";
const toolEnabled$ = bindValue<boolean>(bindingGroup, "ToolEnabled", false);
const label = "Dedicated Turn Lanes";
const floatingButtonTheme = getModule(
    "game-ui/common/input/button/floating-icon-button.module.scss",
    "classes"
) as { button: string; icon: string };

export const DedicatedTurnLanesButton = () => {
    const enabled = useValue(toolEnabled$);

    const toggleTool = () => {
        trigger(bindingGroup, "ToggleTool");
    };

    return (
        <Button
            variant="floating"
            theme={floatingButtonTheme}
            src="Media/Game/Icons/Intersections.svg"
            selected={enabled}
            tooltipLabel={label}
            onSelect={toggleTool}
        />
    );
};
