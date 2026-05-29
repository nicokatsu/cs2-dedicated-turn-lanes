import { bindValue, trigger, useValue } from "cs2/api";
import { getModule } from "cs2/modding";
import { Button } from "cs2/ui";

const toolEnabled$ = bindValue<boolean>("PocketTurnLanes", "ToolEnabled", false);
const label = "Pocket Turn Lanes";
const floatingButtonTheme = getModule(
    "game-ui/common/input/button/floating-icon-button.module.scss",
    "classes"
) as { button: string; icon: string };

export const PocketTurnLanesButton = () => {
    const enabled = useValue(toolEnabled$);

    const toggleTool = () => {
        trigger("PocketTurnLanes", "ToggleTool");
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
