from __future__ import annotations

import json
import unittest

from revit_model_mcp.revit_channel import ReadJob, parse_response


class UniversalReadJobTests(unittest.TestCase):
    def test_forms_query_elements_job_with_combined_filters(self) -> None:
        job = ReadJob.query_elements(
            categories=["Rooms", " rooms "],
            level=" Level 3 ",
            view="Level 3 Plan",
            parameter_filters=[
                {
                    "parameter": " Building Number ",
                    "operator": " EMPTY ",
                }
            ],
            fields=["id", "Building Number"],
            offset=10,
            limit=20,
            sort_field="level",
            sort_direction="desc",
        )

        numeric = ReadJob.query_elements(
            parameter_filters=[{"parameter": "Thickness", "operator": "greater", "value": 200}]
        )
        self.assertEqual(numeric.payload["parameterFilters"][0]["value"], "200")

        self.assertEqual(
            job.payload,
            {
                "command": "query-elements",
                "categories": ["Rooms"],
                "level": "Level 3",
                "view": "Level 3 Plan",
                "parameterFilters": [{"parameter": "Building Number", "operator": "empty"}],
                "fields": ["id", "Building Number"],
                "offset": 10,
                "limit": 20,
                "sort": {"field": "level", "direction": "desc"},
            },
        )

    def test_query_geometry_is_opt_in(self) -> None:
        self.assertNotIn("includeGeometry", ReadJob.query_elements().payload)
        self.assertNotIn("includeGeometry", ReadJob.query_elements(include_geometry=False).payload)
        self.assertTrue(ReadJob.query_elements(include_geometry=True).payload["includeGeometry"])

    def test_response_parser_preserves_geometry_for_details_and_query(self) -> None:
        bounds = {"minMm": [-100, 0, 0], "maxMm": [100, 200, 3000], "centerMm": [0, 100, 1500]}
        point = {
            "location": {"type": "point", "xMm": 0, "yMm": 100.1, "zMm": 0},
            "boundingBox": bounds,
            "roomCenterMm": [0, 100.1, 0],
        }
        curve = {
            "location": {
                "type": "curve",
                "startMm": [0, 0, 0],
                "endMm": [1000.2, 0, 0],
                "lengthMm": 1000.2,
            },
            "boundingBox": bounds,
        }
        for command, data in (
            ("element-details", point),
            ("element-details", curve),
            ("query-elements", {"elements": [dict(id=1, **point), dict(id=2, **curve), {"id": 3}]}),
        ):
            response = {"command": command, "success": True, "data": data}
            self.assertEqual(parse_response(json.dumps(response), command), response)

    def test_forms_aggregate_elements_job(self) -> None:
        job = ReadJob.aggregate_elements(
            ["level"],
            "Area",
            categories=["Areas"],
            area_scheme="Gross Building",
        )

        self.assertEqual(job.payload["groupBy"], ["level"])
        self.assertEqual(job.payload["numericField"], "Area")
        self.assertEqual(job.payload["areaScheme"], "Gross Building")

    def test_forms_catalog_warnings_and_relations_jobs(self) -> None:
        self.assertEqual(
            ReadJob.list_catalog(" parameters ").payload,
            {"command": "list-catalog", "section": "parameters"},
        )
        self.assertEqual(
            ReadJob.list_warnings(" Walls overlap ", True).payload,
            {
                "command": "list-warnings",
                "warningText": "Walls overlap",
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
            ("level-rooms", None, "Level 1", "sourceName", "Level 1"),
            ("group-elements", 42, None, "sourceId", 42),
            ("nested-family", 43, None, "sourceId", 43),
            ("area-scheme-elements", None, "Architecture", "sourceName", "Architecture"),
            (
                "view-template-dependents",
                None,
                "Floor Plan Template",
                "sourceName",
                "Floor Plan Template",
            ),
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
