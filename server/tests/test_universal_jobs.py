from __future__ import annotations

import unittest

from revit_model_mcp.revit_channel import ReadJob


class UniversalReadJobTests(unittest.TestCase):
    def test_forms_query_elements_job_with_combined_filters(self) -> None:
        job = ReadJob.query_elements(
            categories=["Помещения", " помещения "],
            level=" 03 ",
            view="План 3",
            parameter_filters=[
                {
                    "parameter": " ADSK_Номер корпуса ",
                    "operator": " EMPTY ",
                }
            ],
            fields=["id", "ADSK_Номер корпуса"],
            offset=10,
            limit=20,
            sort_field="level",
            sort_direction="desc",
        )

        numeric = ReadJob.query_elements(
            parameter_filters=[
                {"parameter": "Толщина", "operator": "greater", "value": 200}
            ]
        )
        self.assertEqual(numeric.payload["parameterFilters"][0]["value"], "200")

        self.assertEqual(
            job.payload,
            {
                "command": "query-elements",
                "categories": ["Помещения"],
                "level": "03",
                "view": "План 3",
                "parameterFilters": [
                    {"parameter": "ADSK_Номер корпуса", "operator": "empty"}
                ],
                "fields": ["id", "ADSK_Номер корпуса"],
                "offset": 10,
                "limit": 20,
                "sort": {"field": "level", "direction": "desc"},
            },
        )

    def test_forms_aggregate_elements_job(self) -> None:
        job = ReadJob.aggregate_elements(
            ["level"],
            "Площадь",
            categories=["Зоны"],
            area_scheme="СПП в ГНС",
        )

        self.assertEqual(job.payload["groupBy"], ["level"])
        self.assertEqual(job.payload["numericField"], "Площадь")
        self.assertEqual(job.payload["areaScheme"], "СПП в ГНС")

    def test_forms_catalog_warnings_and_relations_jobs(self) -> None:
        self.assertEqual(
            ReadJob.list_catalog(" parameters ").payload,
            {"command": "list-catalog", "section": "parameters"},
        )
        self.assertEqual(
            ReadJob.list_warnings(" Стены пересекаются ", True).payload,
            {
                "command": "list-warnings",
                "warningText": "Стены пересекаются",
                "includeElements": True,
            },
        )
        self.assertEqual(
            ReadJob.list_relations("group-elements", source_id=42).payload,
            {
                "command": "list-relations",
                "relation": "group-elements",
                "sourceId": 42,
            },
        )
        cases = (
            ("level-rooms", None, "01", "sourceName", "01"),
            ("group-elements", 42, None, "sourceId", 42),
            ("nested-family", 43, None, "sourceId", 43),
            ("area-scheme-elements", None, "АР", "sourceName", "АР"),
            ("view-template-dependents", None, "АР шаблон", "sourceName", "АР шаблон"),
        )
        for relation, source_id, source_name, key, expected in cases:
            with self.subTest(relation=relation):
                payload = ReadJob.list_relations(relation, source_id, source_name).payload
                self.assertEqual(payload["relation"], relation)
                self.assertEqual(payload[key], expected)

    def test_rejects_invalid_universal_query_controls(self) -> None:
        with self.assertRaisesRegex(Exception, "sort_direction"):
            ReadJob.query_elements(sort_direction="sideways")
        with self.assertRaisesRegex(Exception, "group_by"):
            ReadJob.aggregate_elements([])


if __name__ == "__main__":
    unittest.main()
