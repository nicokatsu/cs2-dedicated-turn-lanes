import { ModuleRegistry } from "cs2/modding";
import type { ButtonTheme, DropdownTheme } from "cs2/ui";

export type VanillaUiModules = {
    panelTheme?: Record<string, string>;
    selectedInfoPanelTheme?: Record<string, string>;
    selectedInfoPanelClasses?: Record<string, string>;
    dropdownTheme?: DropdownTheme;
    transparentButtonTheme?: Partial<ButtonTheme>;
    activeInfoviewPanelClasses?: ActiveInfoviewPanelClasses;
    infomodeItemClasses?: InfomodeItemClasses;
};

export type ActiveInfoviewPanelClasses = {
    activeInfoviewPanel?: string;
    infomodesPanel?: string;
    scrollable?: string;
    title?: string;
};

export type InfomodeItemClasses = {
    infomodeItem?: string;
    active?: string;
    activeOpacity?: string;
    header?: string;
    title?: string;
    titleText?: string;
    type?: string;
};

const PANEL_THEME_MODULE = "game-ui/common/panel/themes/default.module.scss";
const SELECTED_INFO_PANEL_THEME_MODULE = "game-ui/game/themes/selected-info-panel.module.scss";
const SELECTED_INFO_PANEL_CLASSES_MODULE = "game-ui/game/components/selected-info-panel/selected-info-panel.module.scss";
const DROPDOWN_THEME_MODULE = "game-ui/game/themes/game-dropdown.module.scss";
const TRANSPARENT_BUTTON_THEME_MODULE = "game-ui/game/themes/transparent-button.module.scss";
const ACTIVE_INFOVIEW_PANEL_CLASSES_MODULE =
    "game-ui/game/components/infoviews/active-infoview-panel/active-infoview-panel.module.scss";
const INFOMODE_ITEM_CLASSES_MODULE =
    "game-ui/game/components/infoviews/active-infoview-panel/components/infomode-item/infomode-item.module.scss";

function getOptionalClasses<T>(
    moduleRegistry: ModuleRegistry,
    modulePath: string
): T | undefined {
    return moduleRegistry.get(modulePath, "classes") as T | undefined;
}

export function resolveVanillaUiModules(moduleRegistry: ModuleRegistry): VanillaUiModules {
    return {
        panelTheme: getOptionalClasses<Record<string, string>>(moduleRegistry, PANEL_THEME_MODULE),
        selectedInfoPanelTheme: getOptionalClasses<Record<string, string>>(
            moduleRegistry,
            SELECTED_INFO_PANEL_THEME_MODULE
        ),
        selectedInfoPanelClasses: getOptionalClasses<Record<string, string>>(
            moduleRegistry,
            SELECTED_INFO_PANEL_CLASSES_MODULE
        ),
        dropdownTheme: getOptionalClasses<DropdownTheme>(moduleRegistry, DROPDOWN_THEME_MODULE),
        transparentButtonTheme: getOptionalClasses<Partial<ButtonTheme>>(
            moduleRegistry,
            TRANSPARENT_BUTTON_THEME_MODULE
        ),
        activeInfoviewPanelClasses: getOptionalClasses<ActiveInfoviewPanelClasses>(
            moduleRegistry,
            ACTIVE_INFOVIEW_PANEL_CLASSES_MODULE
        ),
        infomodeItemClasses: getOptionalClasses<InfomodeItemClasses>(
            moduleRegistry,
            INFOMODE_ITEM_CLASSES_MODULE
        ),
    };
}
