-- ─────────────────────────────────────────────────────────────────────────────
-- 0010 流程模型 key 唯一约束（已有库升级用；新库已在 0002 建好）
--
-- 背景：bpmn_model 原先只有普通索引 ix_bpmn_model_key，同一租户下允许出现多个
--       相同 process_key 的模型。发布后这些模型都会往同一个 key 下追加定义版本，
--       导致「发布历史对不上 / 回滚到错误的模型」，控制台也无法判断哪个才是编辑源。
--
-- 幂等：索引已存在时跳过（可重复执行）。
--
-- 前置检查：库里若已有重复 (tenant_id, process_key)，必须先人工合并 / 删除重复模型，
--           否则唯一索引创建会失败。检查语句：
--
--   select tenant_id, process_key, count(*) as cnt, group_concat(id) as model_ids
--     from bpmn_model
--    group by tenant_id, process_key
--   having count(*) > 1;
-- ─────────────────────────────────────────────────────────────────────────────

set @exists := (
    select count(*)
      from information_schema.statistics
     where table_schema = database()
       and table_name = 'bpmn_model'
       and index_name = 'ux_bpmn_model_key'
);

set @sql := if(
    @exists = 0,
    'create unique index ux_bpmn_model_key on bpmn_model (tenant_id, process_key)',
    'select 1'
);

prepare stmt from @sql;
execute stmt;
deallocate prepare stmt;
