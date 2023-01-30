import { FontAwesomeIcon } from '@fortawesome/react-fontawesome'
import * as AppContext from '@framework/AppContext'
import { useAPI } from '@framework/Hooks'
import * as Navigator from '@framework/Navigator'
import { getTypeInfo } from '@framework/Reflection'
import { getToString, is } from '@framework/Signum.Entities'
import * as React from 'react'
import { Nav } from 'react-bootstrap'
import { PermissionSymbol } from '../Authorization/Signum.Entities.Authorization'
import { ToolbarNavItem } from '../Toolbar/Renderers/ToolbarRenderer'
import { IconColor, ToolbarConfig, ToolbarResponse } from '../Toolbar/ToolbarClient'
import { CaseActivityQuery, WorkflowEntity, WorkflowMainEntityStrategy, WorkflowPermission } from '../Workflow/Signum.Entities.Workflow'
import * as WorkflowClient from '../Workflow/WorkflowClient'

export default class WorkflowToolbarMenuConfig extends ToolbarConfig<PermissionSymbol> {

  constructor() {
    var type = PermissionSymbol;
    super(type);
  }

  getDefaultIcon(): IconColor {
    return ({
      icon: "shuffle",
      iconColor: "#2471A3",
    });
  }

  isApplicableTo(element: ToolbarResponse<PermissionSymbol>) {
    return is(element.content, WorkflowPermission.WorkflowToolbarMenu);
  }

  getMenuItem(res: ToolbarResponse<PermissionSymbol>, isActive: boolean, key: number | string) {
    return <WorkflowDropdownImp key={ key}/>
  }

  isCompatibleWithUrlPrio() {
    return 0;
  }

  navigateTo() {
    return Promise.resolve("");
  }
}


function WorkflowDropdownImp() {
  var [show, setShow] = React.useState(false);

  var starts = useAPI(signal => WorkflowClient.API.starts(), []);

  function getStarts(starts: WorkflowEntity[]) {
    return starts.flatMap(w => {
      const typeInfo = getTypeInfo(w.mainEntityType!.cleanName);

      return w.mainEntityStrategies.flatMap(ws => [({ workflow: w, typeInfo, mainEntityStrategy: ws.element! })]);
    }).filter(kvp => !!kvp.typeInfo)
      .groupBy(kvp => kvp.typeInfo.name);
  }

  if (!starts)
    return null;

  return (
    <div>
      {starts.length == 0 &&
        <ToolbarNavItem title={CaseActivityQuery.Inbox.niceName()}
          active={location.href.contains("/find/Inbox")}
          onClick={(e: React.MouseEvent<any>) => { AppContext.pushOrOpenInTab(Options.getInboxUrl()!, e); }}
          icon={ToolbarConfig.coloredIcon("inbox", "steelblue")} />
      }

      {starts.length > 0 &&
        <>
          <ToolbarNavItem
            title={WorkflowEntity.nicePluralName()}
            onClick={() => setShow(!show)}
            icon={
              <div style={{ display: 'inline-block', position: 'relative' }}>
                <div className="nav-arrow-icon" style={{ position: 'absolute' }}><FontAwesomeIcon icon={show ? "caret-down" : "caret-right"} className="icon" /></div>
                <div className="nav-icon-with-arrow">
                  {ToolbarConfig.coloredIcon("random", "mediumvioletred")}
                </div>
              </div>
            } />

          <div style={{ display: show ? "block" : "none" }}>

            <ToolbarNavItem title={CaseActivityQuery.Inbox.niceName()}
              active={location.href.contains("/find/Inbox")}
              onClick={(e: React.MouseEvent<any>) => { AppContext.pushOrOpenInTab(Options.getInboxUrl()!, e); }}
              icon={ToolbarConfig.coloredIcon("inbox", "steelblue")} />

            {getStarts(starts).flatMap((kvp, i) => kvp.elements.map((val, j) =>
              <ToolbarNavItem key={i + "-" + j} title={getToString(val.workflow) + (val.mainEntityStrategy == "CreateNew" ? "" : ` (${WorkflowMainEntityStrategy.niceToString(val.mainEntityStrategy)})`)}
                onClick={(e: React.MouseEvent<any>) => { AppContext.pushOrOpenInTab(`~/workflow/new/${val.workflow.id}/${val.mainEntityStrategy}`, e); }}
                active={false}
                icon={ToolbarConfig.coloredIcon("square-plus", "seagreen")}
              />)
            )}
          </div>
        </>
      }
    </div>
  );
}

export namespace Options {
  export function getInboxUrl(): string {
    return WorkflowClient.getDefaultInboxUrl();
  }
}
