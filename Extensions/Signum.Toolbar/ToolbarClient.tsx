import { RouteObject } from 'react-router'
import { IconProp } from '@fortawesome/fontawesome-svg-core'
import { ajaxGet } from '@framework/Services';
import { Navigator, EntitySettings } from '@framework/Navigator'
import * as AppContext from '@framework/AppContext'
import { Finder } from '@framework/Finder'
import { Lite, Entity } from '@framework/Signum.Entities'
import { ToolbarEntity, ToolbarMenuEntity, ToolbarElementEmbedded, ToolbarElementType, ToolbarLocation, ShowCount, ToolbarSwitcherEntity, ToolbarSwitcherOptionEmbedded } from './Signum.Toolbar'
import { Constructor } from '@framework/Constructor'
import { UserAssetClient } from '../Signum.UserAssets/UserAssetClient'
import { Dic } from '@framework/Globals';
import QueryToolbarConfig from './QueryToolbarConfig';
import { ToolbarConfig } from './ToolbarConfig';
import { ChangeLogClient } from '@framework/Basics/ChangeLogClient';

export namespace ToolbarClient {
  
  export function start(options: { routes: RouteObject[] }): void {
  
    ChangeLogClient.registerChangeLogModule("Signum.Toolbar", () => import("./Changelog"));
  
    Navigator.addSettings(new EntitySettings(ToolbarEntity, t => import('./Templates/Toolbar')));
    Navigator.addSettings(new EntitySettings(ToolbarMenuEntity, t => import('./Templates/ToolbarMenu')));
    Navigator.addSettings(new EntitySettings(ToolbarElementEmbedded, t => import('./Templates/ToolbarElement')));
    Navigator.addSettings(new EntitySettings(ToolbarSwitcherEntity, t => import('./Templates/ToolbarSwitcher')));
    Navigator.addSettings(new EntitySettings(ToolbarSwitcherOptionEmbedded, t => import('./Templates/ToolbarSwitcherOption')));
  
    registerConfig(new QueryToolbarConfig());
  
    Finder.addSettings({ queryName: ToolbarEntity, defaultOrders: [{ token: ToolbarEntity.token(a => a.priority), orderType: "Descending" }] });
  
    Constructor.registerConstructor(ToolbarElementEmbedded, tn => ToolbarElementEmbedded.New({ type: "Item" }));
  
    AppContext.clearSettingsActions.push(cleanConfigs);
  
    UserAssetClient.start({ routes: options.routes });
    UserAssetClient.registerExportAssertLink(ToolbarEntity);
  }
  
  export function cleanConfigs(): void {
    Dic.clear(configs);
  }
  
  
  
  export const configs: { [type: string]: ToolbarConfig<any>[] } = {};
  
  export function registerConfig<T extends Entity>(config: ToolbarConfig<T>): void {
    (configs[config.type.typeName] ??= []).push(config);
  }
  
  export function getConfig(res: ToolbarResponse<any>): ToolbarConfig<any> | null {
    return configs[res.content!.EntityType]?.filter(c => c.isApplicableTo(res)).singleOrNull();
  }
  
  export namespace API {
    export function getCurrentToolbar(location: ToolbarLocation): Promise<ToolbarResponse<any> | null> {
      return ajaxGet({ url: `/api/toolbar/current/${location}` });
    }
  }
 
}

export interface ToolbarResponse<T extends Entity> {
  type: ToolbarElementType;
  label?: string;
  content?: Lite<T>;
  entityType?: string;
  url?: string;
  iconName?: string;
  iconColor?: string;
  showCount?: ShowCount;
  autoRefreshPeriod?: number;
  openInPopup?: boolean;
  elements?: Array<ToolbarResponse<any>>;
  extraIcons?: Array<ToolbarResponse<any>>;
}
