import type { ChangeLogDic } from "@framework/Basics/ChangeLogClient";

export default {
  //"2023-11-13": ["sample change log",],
  "2026-06-15": [
    "WordTemplate xlsx: row-spanning @foreach/@if/@any now supported (parsed at cell level, rows reindexed with formula/merge/reference shifting after render)",
    "WordTemplate xlsx: merged cells inside repeated @foreach rows are now kept per clone (each repeated row gets its own merge) instead of collapsing into one block",
  ],

} as ChangeLogDic;



