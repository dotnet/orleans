import React from 'react';

interface MenuSectionProps {
  active?: boolean;
  icon: string;
  name: string;
  path: string;
  isSeparated?: boolean;
}

interface MenuItem {
  name: string;
  path: string;
  icon: string;
  active?: boolean;
  isSeparated?: boolean;
}

interface MenuProps {
  menu: MenuItem[];
}

class MenuSection extends React.Component<MenuSectionProps> {
  render() {
    const className = 'nav-item' +
      (this.props.active ? ' menu-open' : '') +
      (this.props.isSeparated ? ' mt-auto' : '');

    return (
      <li className={className}>
        <a href={this.props.path} className={'nav-link' + (this.props.active ? ' active' : '')}>
          <i className={this.props.icon + ' nav-icon'} />
          <p>{this.props.name}</p>
        </a>
      </li>
    );
  }
}

export default class Menu extends React.Component<MenuProps> {
  render() {
    return (
      <ul className="nav nav-pills nav-sidebar flex-column" data-widget="treeview" role="menu" data-accordion="false">
        {this.props.menu.map(x => (
          <MenuSection key={x.name}
            active={x.active}
            icon={x.icon}
            name={x.name}
            path={x.path}
            isSeparated={x.isSeparated}
          />
        ))}
      </ul>
    );
  }
}
