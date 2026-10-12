/**
 * 校验问题清单的渲染（纯 DOM，不引入框架）。
 *
 * 交互约定：点击问题 → 定位到该元素（选中 + 滚动到可见），这样用户不必自己去画布里找。
 */
import type { DraftIssue } from './validation';

export function renderIssues(
  container: HTMLElement,
  issues: DraftIssue[],
  onSelect: (elementId: string) => void,
): void {
  container.replaceChildren();

  if (issues.length === 0) {
    const ok = document.createElement('span');
    ok.className = 'issues-empty';
    ok.textContent = '校验通过：没有发现问题。';
    container.append(ok);

    return;
  }

  const list = document.createElement('ul');
  list.className = 'issues-list';

  for (const issue of issues) {
    const item = document.createElement('li');
    item.className = `issues-item issues-${issue.severity}`;

    const badge = document.createElement('span');
    badge.className = 'issues-badge';
    badge.textContent = issue.severity === 'error' ? '错误' : '警告';

    const text = document.createElement('span');
    text.className = 'issues-text';
    text.textContent = issue.message;

    item.append(badge, text);

    if (issue.elementId.length > 0) {
      item.classList.add('issues-clickable');
      item.title = '点击定位到该元素';
      item.addEventListener('click', () => onSelect(issue.elementId));
    }

    list.append(item);
  }

  container.append(list);
}
