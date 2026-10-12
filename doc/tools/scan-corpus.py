#!/usr/bin/env python3
"""
扫描仓库中的 BPMN 文件，评估有多少可直接用于 NetBpm 子集引擎的对拍验证。

NetBpm 子集 = 顺序流 + 排他网关(分支/合并) + 并行网关(同步) + 基础事件/任务

用法:
    python scan-corpus.py [仓库根目录]        # 默认 E:/DataCode/Activiti-9.0.0
"""
import os
import re
import sys
import collections

ROOT = sys.argv[1] if len(sys.argv) > 1 else r"E:/DataCode/Activiti-9.0.0"

# 子集外元素：命中任意一个即判定"不可直接使用"
EXCLUDED = {
    "subProcess", "callActivity", "boundaryEvent", "intermediateCatchEvent",
    "intermediateThrowEvent", "eventBasedGateway", "inclusiveGateway",
    "complexGateway", "terminateEventDefinition", "timerEventDefinition",
    "messageEventDefinition", "signalEventDefinition", "errorEventDefinition",
    "compensateEventDefinition", "cancelEventDefinition", "linkEventDefinition",
    "multiInstanceLoopCharacteristics", "receiveTask", "sendTask",
    "businessRuleTask", "transaction", "laneSet", "lane", "dataObject",
    "eventSubProcess", "message", "signal", "error", "escalationEventDefinition",
    "conditionalEventDefinition", "dataStoreReference", "participant",
    "collaboration", "pool", "interface", "operation", "import", "itemDefinition",
}

# 关注的子集内元素
WATCH = ["startEvent", "endEvent", "sequenceFlow", "exclusiveGateway",
         "parallelGateway", "userTask", "serviceTask", "scriptTask",
         "manualTask", "task", "executionListener", "taskListener"]

TAG_RE = re.compile(r"<\s*([A-Za-z_][\w]*)(?::([A-Za-z_][\w]*))?[^>]*?(/?)>")
COND_RE = re.compile(r"conditionExpression")
DEFAULT_RE = re.compile(r'\sdefault\s*=\s*"')


def collect(root):
    out = []
    for dp, _, fns in os.walk(root):
        if ".git" in dp:
            continue
        for fn in fns:
            if fn.endswith((".bpmn", ".bpmn20.xml", ".bpmn.xml")):
                out.append(os.path.join(dp, fn))
    return out


def main():
    files = collect(ROOT)
    pure, blockers = [], collections.Counter()

    for f in files:
        try:
            txt = open(f, encoding="utf-8", errors="ignore").read()
        except OSError:
            continue
        tags = {m.group(2) or m.group(1) for m in TAG_RE.finditer(txt)}
        bad = tags & EXCLUDED
        if bad:
            for b in bad:
                blockers[b] += 1
        else:
            pure.append((f, txt, tags))

    n = len(files)
    print("BPMN 文件总数              :", n)
    print("可直接对拍（仅子集元素）   :", len(pure),
          "(%.1f%%)" % (100.0 * len(pure) / max(1, n)))
    print("含子集外元素               :", n - len(pure))
    print()

    print("=== 子集语料元素覆盖 ===")
    pn = max(1, len(pure))
    cnt = collections.Counter()
    for _, _, tags in pure:
        for w in WATCH:
            if w in tags:
                cnt[w] += 1
    for w in WATCH:
        c = cnt.get(w, 0)
        if c:
            print("  %-18s %4d  (%.0f%%)" % (w, c, 100.0 * c / pn))
    c = sum(1 for _, t, _ in pure if COND_RE.search(t))
    print("  %-18s %4d  (%.0f%%)" % ("条件表达式", c, 100.0 * c / pn))
    c = sum(1 for _, t, _ in pure if DEFAULT_RE.search(t))
    print("  %-18s %4d  (%.0f%%)" % ("default 默认流", c, 100.0 * c / pn))

    print()
    print("=== 结构组合 ===")
    xor = sum(1 for _, _, t in pure if "exclusiveGateway" in t)
    and_ = sum(1 for _, _, t in pure if "parallelGateway" in t)
    print("  仅顺序流（无网关）      :",
          sum(1 for _, _, t in pure
              if "exclusiveGateway" not in t and "parallelGateway" not in t))
    print("  含排他网关（分支/合并） :", xor)
    print("  含并行网关（同步）      :", and_)
    print("  两者都有                :",
          sum(1 for _, _, t in pure
              if "exclusiveGateway" in t and "parallelGateway" in t))

    print()
    print("=== 排除主力（按文件数）===")
    for k, v in blockers.most_common(15):
        print("  %-32s %d" % (k, v))

    print()
    print("说明：网关语义（排他 %d + 并行 %d，混合 0）在现成语料中覆盖很薄，"
          % (xor, and_))
    print("      计划中必须自建约 120 个网关/条件/阻抗用例（见开发计划 §9.3）。")


if __name__ == "__main__":
    main()
